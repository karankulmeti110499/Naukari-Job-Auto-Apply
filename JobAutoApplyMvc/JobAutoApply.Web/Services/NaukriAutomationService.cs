using JobAutoApply.Web.Models;
using JobAutoApply.Web.Options;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Text.RegularExpressions;
using System.Net;

namespace JobAutoApply.Web.Services;

public sealed class NaukriAutomationService : INaukriAutomationService
{
    private readonly NaukriOptions _naukriOptions;
    private readonly ILogger<NaukriAutomationService> _logger;

    public NaukriAutomationService(IOptions<NaukriOptions> naukriOptions, ILogger<NaukriAutomationService> logger)
    {
        _naukriOptions = naukriOptions.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<JobApplyRecord>> SearchAndApplyAsync(JobSearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobTitle))
        {
            throw new InvalidOperationException("Job title is required for searching.");
        }

        var results = new List<JobApplyRecord>();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = request.Headless,
        });

        var storageStatePath = ResolveStorageStatePath(_naukriOptions.StorageStateFilePath);
        var contextOptions = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 900,
            },
        };

        if (File.Exists(storageStatePath))
        {
            contextOptions.StorageStatePath = storageStatePath;
        }

        var context = await browser.NewContextAsync(contextOptions);

        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(_naukriOptions.BaseUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000,
            });

            await EnsureLoggedInAsync(page, request, context, storageStatePath);
            await PerformSearchAsync(page, request.JobTitle, cancellationToken);

            var jobLinks = await CollectJobLinksAsync(page, request.MaxJobs);
            if (jobLinks.Count == 0)
            {
                var debugScreenshotPath = await TryCaptureDebugSnapshotAsync(page);
                var currentUrl = page.Url ?? "unknown";
                var message = $"No job listings were found on Naukri for '{request.JobTitle}'. Current page: {currentUrl}.";
                if (!string.IsNullOrWhiteSpace(debugScreenshotPath))
                {
                    message += $" Debug screenshot: {debugScreenshotPath}";
                }

                throw new InvalidOperationException(message);
            }

            foreach (var link in jobLinks.Take(request.MaxJobs))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var record = await ProcessJobLinkAsync(context, link);
                results.Add(record);
            }

            return results;
        }
        catch (Exception ex)
        {
            var debugScreenshotPath = await TryCaptureDebugSnapshotAsync(page);
            var enrichedMessage = string.IsNullOrWhiteSpace(debugScreenshotPath)
                ? ex.Message
                : $"{ex.Message} Debug screenshot: {debugScreenshotPath}";

            _logger.LogWarning(ex, "Naukri automation failed. {Message}", enrichedMessage);

            if (!request.Headless)
            {
                await page.WaitForTimeoutAsync(2500);
            }

            throw new InvalidOperationException(enrichedMessage, ex);
        }
    }

    private async Task EnsureLoggedInAsync(IPage page, JobSearchRequest request, IBrowserContext context, string storageStatePath)
    {
        if (await IsLoggedInAsync(page))
        {
            await PersistStorageStateAsync(context, storageStatePath);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new InvalidOperationException("Naukri session is not active. Enter Naukri email/password once to save login for future runs.");
        }

        await DismissBlockingLayersAsync(page);

        var loginTrigger = page.Locator("#login_Layer, a:has-text('Login'), a[title='Jobseeker Login'], button:has-text('Login')");
        var loginTriggerTarget = await GetFirstVisibleAsync(loginTrigger);
        if (loginTriggerTarget is not null)
        {
            await loginTriggerTarget.ClickAsync(new LocatorClickOptions
            {
                Force = true,
                Timeout = 5000,
            });

            await page.WaitForTimeoutAsync(1200);
        }

        var emailInput = page.Locator("input[type='email'], input[type='text'][placeholder*='Email' i], input[name='username'], input[id*='username' i]");
        var passwordInput = page.Locator("input[type='password'], input[name='password'], input[id*='password' i]");
        if (await GetFirstVisibleAsync(emailInput) is null || await GetFirstVisibleAsync(passwordInput) is null)
        {
            await page.GotoAsync("https://www.naukri.com/nLogin/Login.php", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000,
            });

            await page.WaitForTimeoutAsync(1500);
            await DismissBlockingLayersAsync(page);
        }

        emailInput = page.Locator("input[type='email'], input[type='text'][placeholder*='Email' i], input[name='username'], input[id*='username' i]");
        passwordInput = page.Locator("input[type='password'], input[name='password'], input[id*='password' i]");
        var emailTarget = await GetFirstVisibleAsync(emailInput);
        var passwordTarget = await GetFirstVisibleAsync(passwordInput);

        if (emailTarget is null || passwordTarget is null)
        {
            throw new InvalidOperationException("Could not open Naukri login form. Please log in manually once and retry.");
        }

        await emailTarget.FillAsync(request.Email.Trim());
        await passwordTarget.FillAsync(request.Password);

        var loginButton = page.Locator("button[type='submit'], button:has-text('Login'), button:has-text('Sign in'), button:has-text('Continue')");
        var loginButtonTarget = await GetFirstVisibleAsync(loginButton);
        if (loginButtonTarget is not null)
        {
            await loginButtonTarget.ClickAsync(new LocatorClickOptions
            {
                Timeout = 7000,
            });
        }
        else
        {
            await passwordTarget.PressAsync("Enter");
        }

        await page.WaitForTimeoutAsync(5000);

        if (await IsLoggedInAsync(page))
        {
            await PersistStorageStateAsync(context, storageStatePath);
            return;
        }

        if (await IsLoginChallengeVisibleAsync(page))
        {
            throw new InvalidOperationException("Naukri login requires OTP/CAPTCHA/manual verification. Please complete login manually in browser and retry.");
        }

        throw new InvalidOperationException("Naukri login did not complete. Check credentials and retry.");
    }

    private static string ResolveStorageStatePath(string configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "Data/NaukriStorageState.json"
            : configuredPath;

        return Path.GetFullPath(path);
    }

    private static async Task PersistStorageStateAsync(IBrowserContext context, string storageStatePath)
    {
        var directoryPath = Path.GetDirectoryName(storageStatePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = storageStatePath,
        });
    }

    private static async Task<bool> IsLoggedInAsync(IPage page)
    {
        var loggedInSelectors = new[]
        {
            "a[title*='Profile' i]",
            "a[href*='mnjuser/profile']",
            "div.nI-gNb-info__label",
            "img.nI-gNb-icon-img",
            "text=Logout",
        };

        foreach (var selector in loggedInSelectors)
        {
            if (await IsFirstVisibleAsync(page.Locator(selector)))
            {
                return true;
            }
        }

        var loginSelectors = new[]
        {
            "#login_Layer",
            "a:has-text('Login')",
            "a[title='Jobseeker Login']",
            "button:has-text('Login')",
        };

        foreach (var selector in loginSelectors)
        {
            if (await IsFirstVisibleAsync(page.Locator(selector)))
            {
                return false;
            }
        }

        return !page.Url.Contains("nlogin/login", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsLoginChallengeVisibleAsync(IPage page)
    {
        var challengeSelectors = new[]
        {
            "input[name*='otp' i]",
            "input[placeholder*='otp' i]",
            "iframe[title*='captcha' i]",
            "div[class*='captcha' i]",
            "text=Enter OTP",
        };

        foreach (var selector in challengeSelectors)
        {
            if (await IsFirstVisibleAsync(page.Locator(selector)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<ILocator?> GetFirstVisibleAsync(ILocator locator)
    {
        var count = await locator.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var candidate = locator.Nth(i);
            if (await candidate.IsVisibleAsync())
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task PerformSearchAsync(IPage page, string jobTitle, CancellationToken cancellationToken)
    {
        var normalizedJobTitle = NormalizeSearchJobTitle(jobTitle);

        if (await TrySearchFromVisibleInputAsync(page, normalizedJobTitle, cancellationToken))
        {
            return;
        }

        if (await TryNavigateDirectlyToSearchResultsAsync(page, normalizedJobTitle, cancellationToken))
        {
            return;
        }

        throw new InvalidOperationException("Could not locate the Naukri job search input.");
    }

    private static async Task<bool> TrySearchFromVisibleInputAsync(IPage page, string jobTitle, CancellationToken cancellationToken)
    {
        var inputSelectors = new[]
        {
            "input[placeholder*='skills' i]",
            "input[placeholder*='designation' i]",
            "input[placeholder*='job title' i]",
            "input[placeholder*='keyword' i]",
            "input[name='qsb-keyword-sugg']",
            "#root input.suggestor-input:not([placeholder*='location' i])",
            "input[placeholder*='Search jobs']",
            "input[placeholder*='Enter skills']",
        };

        foreach (var selector in inputSelectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DismissBlockingLayersAsync(page);

            var locator = page.Locator(selector);
            var count = await locator.CountAsync();
            if (count == 0)
            {
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                var input = locator.Nth(i);
                if (!await input.IsVisibleAsync())
                {
                    continue;
                }

                var placeholder = (await input.GetAttributeAsync("placeholder")) ?? string.Empty;
                var name = (await input.GetAttributeAsync("name")) ?? string.Empty;
                var id = (await input.GetAttributeAsync("id")) ?? string.Empty;
                if (IsLocationInput(placeholder, name, id))
                {
                    continue;
                }

                var typed = await TrySetSearchInputAsync(page, input, jobTitle);
                if (!typed)
                {
                    continue;
                }

                await page.Keyboard.PressAsync("Enter");

                if (await WaitForJobResultsAsync(page, cancellationToken))
                {
                    return true;
                }

                var submitButton = page.Locator("button.qsbSubmit, button[type='submit'], button:has-text('Search')");
                if (await submitButton.CountAsync() > 0 && await submitButton.First.IsVisibleAsync())
                {
                    await submitButton.First.ClickAsync();
                    if (await WaitForJobResultsAsync(page, cancellationToken))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsLocationInput(string placeholder, string name, string id)
    {
        var combined = $"{placeholder} {name} {id}";
        return combined.Contains("location", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("city", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TrySetSearchInputAsync(IPage page, ILocator input, string jobTitle)
    {
        try
        {
            await input.FocusAsync();
            await input.FillAsync(string.Empty, new LocatorFillOptions { Timeout = 4000 });
            await input.FillAsync(jobTitle, new LocatorFillOptions { Timeout = 5000 });
            return true;
        }
        catch
        {
            try
            {
                await input.EvaluateAsync("(el, value) => { el.focus(); el.value = ''; el.dispatchEvent(new Event('input', { bubbles: true })); el.value = value; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); }", jobTitle);
                await page.WaitForTimeoutAsync(300);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task DismissBlockingLayersAsync(IPage page)
    {
        await page.Keyboard.PressAsync("Escape");

        var closeButtons = page.Locator("button[aria-label='close'], button[aria-label='Close'], .crossIcon, .drawer-close, button:has-text('Close'), button:has-text('Skip')");
        if (await closeButtons.CountAsync() > 0)
        {
            try
            {
                await closeButtons.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 1200 });
            }
            catch
            {
                // Best-effort only.
            }
        }

        await page.EvaluateAsync(@"() => {
            const selectors = ['.drawer-overlay', '.nI-gNb-log-reg', '.modal-backdrop', '.ReactModal__Overlay'];
            for (const selector of selectors) {
                document.querySelectorAll(selector).forEach((node) => {
                    if (!(node instanceof HTMLElement)) return;
                    node.style.pointerEvents = 'none';
                });
            }
        }");
    }

    private async Task<bool> TryNavigateDirectlyToSearchResultsAsync(IPage page, string jobTitle, CancellationToken cancellationToken)
    {
        var normalizedTitle = NormalizeSearchJobTitle(jobTitle);
        var slugSource = Regex.Replace(normalizedTitle, @"\.net", "dot net", RegexOptions.IgnoreCase);
        var slug = Regex.Replace(slugSource.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var query = WebUtility.UrlEncode(normalizedTitle.Trim());
        var baseUrl = _naukriOptions.BaseUrl.TrimEnd('/');
        var candidateUrls = new List<string>
        {
            $"{baseUrl}/jobs?k={query}",
            $"{baseUrl}/jobs-in-india?k={query}",
        };

        if (!string.IsNullOrWhiteSpace(slug))
        {
            candidateUrls.Add($"{baseUrl}/{slug}-jobs");
            candidateUrls.Add($"{baseUrl}/{slug}-jobs-in-india");
        }

        if (normalizedTitle.Contains(".NET", StringComparison.OrdinalIgnoreCase))
        {
            candidateUrls.Add($"{baseUrl}/dot-net-developer-jobs");
            candidateUrls.Add($"{baseUrl}/dot-net-developer-jobs-in-india");
        }

        foreach (var searchUrl in candidateUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000,
            });

            var correctedUrl = TryFixMalformedSearchUrl(page.Url);
            if (!string.IsNullOrWhiteSpace(correctedUrl)
                && !string.Equals(correctedUrl, page.Url, StringComparison.OrdinalIgnoreCase))
            {
                await page.GotoAsync(correctedUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000,
                });
            }

            if (await WaitForJobResultsAsync(page, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static string TryFixMalformedSearchUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var fixedUrl = Regex.Replace(url, "-20(?=(developer|engineer|architect|analyst|tester|consultant|programmer|manager|lead)(-|$))", "-", RegexOptions.IgnoreCase);
        fixedUrl = Regex.Replace(fixedUrl, "20(?=(developer|engineer|architect|analyst|tester|consultant|programmer|manager|lead)(-|$))", string.Empty, RegexOptions.IgnoreCase);
        return fixedUrl;
    }

    private static string NormalizeSearchJobTitle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var value = WebUtility.UrlDecode(input).Trim();
        value = Regex.Replace(value, "\\s+", " ");
        value = Regex.Replace(value, @"\b20(?=(developer|engineer|architect|analyst|tester|consultant|programmer|manager|lead)\b)", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bdot\s*[- ]*net\b", ".NET", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\basp\.?\s*[- ]*net\b", "ASP.NET", RegexOptions.IgnoreCase);
        return Regex.Replace(value, "\\s+", " ").Trim();
    }

    private static async Task<bool> WaitForJobResultsAsync(IPage page, CancellationToken cancellationToken)
    {
        var resultSelectors = new[]
        {
            "a.title",
            "a[title][href*='/job-listings-']",
            "a[href*='/job-listings']",
            "a[href*='-jobs']",
            "article.jobTuple",
            "div.cust-job-tuple",
            "div.srp-jobtuple-wrapper",
            "div[aria-label='Search Results'] a[href*='/job-listings-']",
        };

        for (var i = 0; i < 20; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var selector in resultSelectors)
            {
                if (await page.Locator(selector).CountAsync() > 0)
                {
                    return true;
                }
            }

            await page.WaitForTimeoutAsync(400);
        }

        return false;
    }

    private static async Task<string> TryCaptureDebugSnapshotAsync(IPage page)
    {
        try
        {
            var folderPath = Path.Combine(AppContext.BaseDirectory, "DebugArtifacts");
            Directory.CreateDirectory(folderPath);

            var fileName = $"naukri-search-error-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            var fullPath = Path.Combine(folderPath, fileName);

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = fullPath,
                FullPage = true,
            });

            return fullPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<List<string>> CollectJobLinksAsync(IPage page, int maxJobs)
    {
        var targetCount = Math.Max(maxJobs, 1);
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < 8; i++)
        {
            var links = await page.EvaluateAsync<string[]>(@"() => {
                const selectors = [
                    'a.title[href]',
                    'a[title][href*=""job-listings""]',
                    'a[href*=""/job-listings-""]',
                    'a[href*=""job-listings""]',
                    'article a[href]',
                    'div.cust-job-tuple a[href]',
                    'div.srp-jobtuple-wrapper a[href]'
                ];

                const seen = new Set();
                for (const selector of selectors) {
                    const nodes = document.querySelectorAll(selector);
                    nodes.forEach((node) => {
                        if (node && node.href) {
                            seen.add(node.href);
                        }
                    });
                }

                return Array.from(seen);
            }");

            foreach (var link in links)
            {
                var normalized = NormalizeJobLink(link);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                discovered.Add(normalized);
            }

            if (discovered.Count >= targetCount)
            {
                break;
            }

            await page.Mouse.WheelAsync(0, 1800);
            await page.WaitForTimeoutAsync(700);
        }

        if (discovered.Count == 0)
        {
            var html = await page.ContentAsync();
            var regexMatches = Regex.Matches(html, @"https://www\.naukri\.com/job-listings-[^\""""'<>\s]+");
            foreach (Match match in regexMatches)
            {
                var decoded = WebUtility.HtmlDecode(match.Value);
                var normalized = NormalizeJobLink(decoded);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    discovered.Add(normalized);
                }
            }
        }

        return discovered.Take(targetCount).ToList();
    }

    private static string NormalizeJobLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return string.Empty;
        }

        var value = link.Trim();
        if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (!value.Contains("naukri.com", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (!value.Contains("job-listings", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (value.Contains("/jobs-in-", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/mnjuser/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/companies", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var hashIndex = value.IndexOf('#');
        if (hashIndex >= 0)
        {
            value = value[..hashIndex];
        }

        return value;
    }

    private async Task<JobApplyRecord> ProcessJobLinkAsync(IBrowserContext context, string link)
    {
        var page = await context.NewPageAsync();

        try
        {
            await page.GotoAsync(link, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000,
            });

            var record = new JobApplyRecord
            {
                JobUrl = link,
                JobTitle = await GetFirstTextAsync(page, "h1", "h1.av-special-heading-tag"),
                Company = await GetFirstTextAsync(page, "a.comp-name", ".styles_jd-header-comp-name__MvqAI", "span.comp-name"),
                Location = await GetFirstTextAsync(page, ".styles_jhc__location__W_pVs", ".locWdth", "span.location"),
            };

            var hasApplyOnCompanySite = await ContainsActionTextAsync(page, "Apply on company site");
            var hasDirectApply = await ContainsExactActionTextAsync(page, "Apply");

            if (hasDirectApply)
            {
                var clicked = await ClickDirectApplyAsync(page);
                record.ApplyType = "Apply";
                record.Status = clicked ? "Apply clicked" : "Apply button found";
                record.Notes = "Applied through Naukri when possible.";
            }
            else if (hasApplyOnCompanySite)
            {
                record.ApplyType = "Apply on company site";
                record.Status = "External apply required";
                record.Notes = "Saved for manual apply on company website.";
            }
            else
            {
                record.ApplyType = "No apply option";
                record.Status = "Skipped";
                record.Notes = "No known apply button found on job detail page.";
            }

            return record;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while processing job URL: {JobUrl}", link);
            return new JobApplyRecord
            {
                JobUrl = link,
                ApplyType = "Error",
                Status = "Failed",
                Notes = ex.Message,
            };
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task<string> GetFirstTextAsync(IPage page, params string[] selectors)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            var value = await locator.First.InnerTextAsync();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static async Task<bool> ContainsActionTextAsync(IPage page, string actionText)
    {
        var locator = page.Locator($"text={actionText}");
        return await IsFirstVisibleAsync(locator);
    }

    private static async Task<bool> ContainsExactActionTextAsync(IPage page, string actionText)
    {
        var locator = page.Locator("button, a");
        var count = await locator.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var text = (await locator.Nth(i).InnerTextAsync()).Trim();
            if (text.Equals(actionText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> ClickDirectApplyAsync(IPage page)
    {
        var locator = page.Locator("button, a");
        var count = await locator.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var element = locator.Nth(i);
            var text = (await element.InnerTextAsync()).Trim();
            if (!text.Equals("Apply", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!await element.IsVisibleAsync())
            {
                continue;
            }

            await element.ClickAsync();
            await page.WaitForTimeoutAsync(1000);
            return true;
        }

        return false;
    }

    private static async Task<bool> IsFirstVisibleAsync(ILocator locator)
    {
        if (await locator.CountAsync() == 0)
        {
            return false;
        }

        try
        {
            return await locator.First.IsVisibleAsync();
        }
        catch
        {
            return false;
        }
    }
}
