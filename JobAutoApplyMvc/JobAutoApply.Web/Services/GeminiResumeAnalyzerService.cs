using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobAutoApply.Web.Models;
using JobAutoApply.Web.Options;
using Microsoft.Extensions.Options;

namespace JobAutoApply.Web.Services;

public sealed class GeminiResumeAnalyzerService : IGeminiResumeAnalyzerService
{
    private static readonly string[] RoleKeywords =
    [
        "developer", "engineer", "architect", "analyst", "tester", "consultant", "programmer", "manager", "lead", "administrator", "specialist", "intern", "sde",
    ];

    private static readonly string[] KnownTitles =
    [
        ".NET Developer",
        "ASP.NET Developer",
        "Software Engineer",
        "Software Developer",
        "Full Stack Developer",
        "Backend Developer",
        "Front End Developer",
        "Web Developer",
        "DevOps Engineer",
        "QA Engineer",
        "Data Engineer",
        "Data Analyst",
        "Python Developer",
        "Java Developer",
        "C# Developer",
        "Application Developer",
    ];

    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _geminiOptions;
    private readonly ILogger<GeminiResumeAnalyzerService> _logger;

    public GeminiResumeAnalyzerService(
        HttpClient httpClient,
        IOptions<GeminiOptions> geminiOptions,
        ILogger<GeminiResumeAnalyzerService> logger)
    {
        _httpClient = httpClient;
        _geminiOptions = geminiOptions.Value;
        _logger = logger;
    }

    public async Task<ResumeAnalysisResult> AnalyzeAsync(string resumeText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            throw new InvalidOperationException("Resume text is empty after extraction.");
        }

        if (string.IsNullOrWhiteSpace(_geminiOptions.ApiKey))
        {
            _logger.LogWarning("Gemini API key is missing; using fallback keyword extraction.");
            return BuildFallbackResult(resumeText);
        }

                var prompt = """
                                         Analyze the following resume text and return ONLY valid JSON in this exact format:
                                         {
                                             "suggestedJobTitle": "...",
                                             "keywords": ["...", "..."]
                                         }

                                         Rules:
                                         - suggestedJobTitle must be the candidate's exact primary role from resume headline/designation/most recent role.
                                         - Keep suggestedJobTitle concise (2-5 words), searchable, and role-like. Example: ".NET Developer", "Software Engineer".
                                         - Do not include years, dates, company names, location, or experience counts in suggestedJobTitle.
                                         - keywords should be the top 8 to 12 relevant skills/technologies.
                                         - Do not include markdown code fences.

                                         Resume Text:
                                         """ + Environment.NewLine + resumeText;

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt },
                    },
                },
            },
        };

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_geminiOptions.Model}:generateContent?key={_geminiOptions.ApiKey}";
        using var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var reason = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Gemini API call failed ({StatusCode}): {Reason}", response.StatusCode, reason);
            return BuildFallbackResult(resumeText);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var responseText = GetModelText(document.RootElement);
        var jsonPayload = ExtractJsonObject(responseText);

        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            _logger.LogWarning("Gemini response could not be parsed as JSON. Falling back.");
            return BuildFallbackResult(resumeText);
        }

        var result = JsonSerializer.Deserialize<ResumeAnalysisResult>(jsonPayload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        if (result is null || string.IsNullOrWhiteSpace(result.SuggestedJobTitle) || result.Keywords.Count == 0)
        {
            _logger.LogWarning("Gemini JSON deserialized to incomplete result. Falling back.");
            return BuildFallbackResult(resumeText);
        }

        return PostProcessResult(resumeText, result);
    }

    public async Task<IReadOnlyCollection<string>> RecommendJobTitlesAsync(string resumeText, ResumeAnalysisResult analysis, CancellationToken cancellationToken)
    {
        var fallback = BuildDynamicFallbackTitles(analysis);

        if (string.IsNullOrWhiteSpace(resumeText) || string.IsNullOrWhiteSpace(_geminiOptions.ApiKey))
        {
            return fallback;
        }

        var prompt = """
                     Analyze this resume summary and suggest job titles suitable for job search.
                     Return ONLY valid JSON in this exact format:
                     {
                       "jobTitleOptions": ["...", "...", "..."]
                     }

                     Rules:
                     - Return 4 to 8 concise, searchable titles.
                     - Each title must be 2 to 5 words.
                     - Do not include company names, locations, years, or experience counts.
                     - Prefer practical hiring titles that match skills and recent role.
                     - Do not include markdown code fences.

                     Existing inferred title:
                     """ + analysis.SuggestedJobTitle + Environment.NewLine + """

                     Extracted skills:
                     """ + string.Join(", ", analysis.Keywords) + Environment.NewLine + """

                     Resume Text:
                     """ + Environment.NewLine + TruncateResumeText(resumeText);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt },
                    },
                },
            },
        };

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_geminiOptions.Model}:generateContent?key={_geminiOptions.ApiKey}";
        using var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var reason = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Gemini title recommendation call failed ({StatusCode}): {Reason}", response.StatusCode, reason);
            return fallback;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var responseText = GetModelText(document.RootElement);
        var jsonPayload = ExtractJsonObject(responseText);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return fallback;
        }

        var aiOptions = ParseJobTitleOptions(jsonPayload);
        if (aiOptions.Count == 0)
        {
            return fallback;
        }

        var merged = new List<string>();
        foreach (var title in aiOptions)
        {
            AddUniqueTitle(merged, title);
        }

        foreach (var title in fallback)
        {
            AddUniqueTitle(merged, title);
        }

        return merged.Take(8).ToList();
    }

    public async Task<string?> SuggestAnswerForApplicationQuestionAsync(string question, string resumeText, ResumeAnalysisResult analysis, IReadOnlyCollection<string> optionHints, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return null;
        }

        var fallbackAnswer = BuildFallbackQuestionAnswer(question, resumeText, analysis, optionHints);
        if (string.IsNullOrWhiteSpace(_geminiOptions.ApiKey))
        {
            return fallbackAnswer;
        }

        var optionsText = optionHints.Count == 0
            ? "N/A"
            : string.Join(", ", optionHints.Take(10));

        var prompt = """
                     You are assisting in answering a job application screening question.
                     Use ONLY the candidate data below.

                     Return ONLY one concise plain-text answer.
                     If the answer cannot be inferred from candidate data, return exactly: NOT_FOUND

                     Screening Question:
                     """ + question + Environment.NewLine + """

                     Available Options (if select/radio):
                     """ + optionsText + Environment.NewLine + """

                     Candidate Suggested Role:
                     """ + analysis.SuggestedJobTitle + Environment.NewLine + """

                     Candidate Skills:
                     """ + string.Join(", ", analysis.Keywords) + Environment.NewLine + """

                     Resume Text:
                     """ + Environment.NewLine + TruncateResumeText(resumeText);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt },
                    },
                },
            },
        };

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{_geminiOptions.Model}:generateContent?key={_geminiOptions.ApiKey}";
        using var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var reason = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Gemini question-answer call failed ({StatusCode}): {Reason}", response.StatusCode, reason);
            return fallbackAnswer;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var responseText = GetModelText(document.RootElement);
        var cleaned = CleanupModelAnswer(responseText);

        if (string.IsNullOrWhiteSpace(cleaned)
            || cleaned.Equals("NOT_FOUND", StringComparison.OrdinalIgnoreCase)
            || cleaned.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            return fallbackAnswer;
        }

        return cleaned;
    }

    private static string GetModelText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        return parts[0].TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
    }

    private static string ExtractJsonObject(string modelResponse)
    {
        if (string.IsNullOrWhiteSpace(modelResponse))
        {
            return string.Empty;
        }

        var start = modelResponse.IndexOf('{');
        var end = modelResponse.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return string.Empty;
        }

        return modelResponse[start..(end + 1)];
    }

    private static ResumeAnalysisResult BuildFallbackResult(string resumeText)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "with", "from", "that", "this", "have", "been", "using", "and", "the", "for", "you", "your", "are", "was", "in", "of", "to", "on", "a", "an", "as", "or", "at", "by",
        };

        var matches = Regex.Matches(resumeText, "[A-Za-z][A-Za-z0-9.+#-]{2,}");
        var keywords = matches
            .Select(x => x.Value.Trim())
            .Where(x => !stopWords.Contains(x))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key)
            .Select(x => x.Key)
            .Take(12)
            .ToList();

        var fallbackResult = new ResumeAnalysisResult
        {
            SuggestedJobTitle = keywords.Count > 0 ? string.Join(' ', keywords.Take(3)) : "Software Engineer",
            Keywords = keywords,
        };

        return PostProcessResult(resumeText, fallbackResult);
    }

    private static ResumeAnalysisResult PostProcessResult(string resumeText, ResumeAnalysisResult result)
    {
        var cleanedTitle = CleanupTitle(result.SuggestedJobTitle);
        var inferredTitle = InferTitleFromResume(resumeText);

        if (!LooksLikeJobTitle(cleanedTitle) || ContainsMalformedRoleNumber(cleanedTitle))
        {
            cleanedTitle = inferredTitle;
        }

        if (!string.IsNullOrWhiteSpace(inferredTitle)
            && inferredTitle.Contains(".NET", StringComparison.OrdinalIgnoreCase)
            && !cleanedTitle.Contains(".NET", StringComparison.OrdinalIgnoreCase))
        {
            cleanedTitle = inferredTitle;
        }

        if (string.IsNullOrWhiteSpace(cleanedTitle))
        {
            cleanedTitle = "Software Engineer";
        }

        var keywords = result.Keywords
            .Select(CleanupKeyword)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        if (keywords.Count == 0)
        {
            keywords = ExtractFallbackKeywords(resumeText);
        }

        return new ResumeAnalysisResult
        {
            SuggestedJobTitle = cleanedTitle,
            Keywords = keywords,
        };
    }

    private static string InferTitleFromResume(string resumeText)
    {
        var lines = resumeText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Regex.Replace(x, "\\s+", " ").Trim())
            .Where(x => x.Length >= 3 && x.Length <= 90)
            .Take(100)
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var topBlock = string.Join(" ", lines.Take(40));
        var candidates = new List<string>();

        var designationPattern = new Regex(@"\b(?:designation|role|position|title|professional\s+headline|current\s+role)\s*[:\-]\s*(?<title>[A-Za-z0-9.+#/\-\s]{3,60})", RegexOptions.IgnoreCase);
        foreach (var line in lines.Take(40))
        {
            var match = designationPattern.Match(line);
            if (match.Success)
            {
                var value = CleanupTitle(match.Groups["title"].Value);
                if (LooksLikeJobTitle(value))
                {
                    candidates.Add(value);
                }
            }
        }

        var knownTitlePattern = string.Join("|", KnownTitles.Select(Regex.Escape));
        var knownMatches = Regex.Matches(topBlock, $@"\b({knownTitlePattern})\b", RegexOptions.IgnoreCase);
        foreach (Match match in knownMatches)
        {
            var value = CleanupTitle(match.Value);
            if (LooksLikeJobTitle(value))
            {
                candidates.Add(value);
            }
        }

        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        var best = candidates
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => topBlock.IndexOf(g.Key, StringComparison.OrdinalIgnoreCase))
            .Select(g => g.First())
            .FirstOrDefault();

        return best ?? string.Empty;
    }

    private static List<string> ExtractFallbackKeywords(string resumeText)
    {
        var matches = Regex.Matches(resumeText, "[A-Za-z][A-Za-z0-9.+#-]{2,}");
        return matches
            .Select(x => x.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static string CleanupTitle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var value = Regex.Replace(input, "\\s+", " ").Trim().Trim('"', '\'', '.', ',', ':', '-', ' ');
        value = Regex.Replace(value, @"\b\d{2,}(developer|engineer|architect|analyst|tester|consultant|programmer|manager|lead)\b", "$1", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\b\d{2,}\s+(?=(developer|engineer|architect|analyst|tester|consultant|programmer|manager|lead)\b)", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bdot\s*net\b", ".NET", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\basp\.?\s*net\b", "ASP.NET", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, "\\s+", " ").Trim();

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 6)
        {
            value = string.Join(' ', words.Take(6));
        }

        return value;
    }

    private static string CleanupKeyword(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return Regex.Replace(input.Trim(), "\\s+", " ").Trim('"', '\'', '.', ',', ';', ':');
    }

    private static IReadOnlyCollection<string> ParseJobTitleOptions(string jsonPayload)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonPayload);
            if (!document.RootElement.TryGetProperty("jobTitleOptions", out var optionsElement)
                || optionsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var options = new List<string>();
            foreach (var item in optionsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                AddUniqueTitle(options, item.GetString());
            }

            return options.Take(8).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyCollection<string> BuildDynamicFallbackTitles(ResumeAnalysisResult analysis)
    {
        var options = new List<string>();

        AddUniqueTitle(options, analysis.SuggestedJobTitle);

        var role = InferRoleToken(analysis.SuggestedJobTitle);
        foreach (var keyword in analysis.Keywords)
        {
            var keywordToken = NormalizeKeywordForTitle(keyword);
            if (string.IsNullOrWhiteSpace(keywordToken))
            {
                continue;
            }

            AddUniqueTitle(options, $"{keywordToken} {role}");
            if (options.Count >= 8)
            {
                break;
            }
        }

        if (options.Count == 0)
        {
            AddUniqueTitle(options, "Software Engineer");
        }

        return options.Take(8).ToList();
    }

    private static void AddUniqueTitle(List<string> options, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var cleaned = CleanupTitle(title);
        if (string.IsNullOrWhiteSpace(cleaned)
            || !LooksLikeJobTitle(cleaned)
            || ContainsMalformedRoleNumber(cleaned)
            || options.Any(x => x.Equals(cleaned, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        options.Add(cleaned);
    }

    private static string InferRoleToken(string? suggestedTitle)
    {
        if (!string.IsNullOrWhiteSpace(suggestedTitle))
        {
            var words = suggestedTitle
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .ToArray();

            var role = words.LastOrDefault(x => RoleKeywords.Contains(x, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(role))
            {
                return char.ToUpperInvariant(role[0]) + role[1..].ToLowerInvariant();
            }
        }

        return "Engineer";
    }

    private static string NormalizeKeywordForTitle(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return string.Empty;
        }

        var value = CleanupKeyword(keyword);
        value = Regex.Replace(value, "[^A-Za-z0-9.+#\\s]", " ");
        value = Regex.Replace(value, "\\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 2)
        {
            return string.Empty;
        }

        if (value.Length < 2 || value.Length > 22)
        {
            return string.Empty;
        }

        value = Regex.Replace(value, @"\bdot\s*net\b", ".NET", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\basp\.?\s*net\b", "ASP.NET", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bc\#\b", "C#", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bai\b", "AI", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bml\b", "ML", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bui\b", "UI", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bux\b", "UX", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bsql\b", "SQL", RegexOptions.IgnoreCase);

        return value;
    }

    private static string? BuildFallbackQuestionAnswer(string question, string resumeText, ResumeAnalysisResult analysis, IReadOnlyCollection<string> optionHints)
    {
        var normalizedQuestion = Regex.Replace(question, "\\s+", " ").Trim();

        if (Regex.IsMatch(normalizedQuestion, "skills?|technologies|stack", RegexOptions.IgnoreCase))
        {
            var answer = string.Join(", ", analysis.Keywords.Take(6));
            return string.IsNullOrWhiteSpace(answer) ? null : answer;
        }

        if (Regex.IsMatch(normalizedQuestion, "current role|designation|job title|position|profile", RegexOptions.IgnoreCase))
        {
            return string.IsNullOrWhiteSpace(analysis.SuggestedJobTitle) ? null : analysis.SuggestedJobTitle;
        }

        if (Regex.IsMatch(normalizedQuestion, "experience|years", RegexOptions.IgnoreCase))
        {
            var years = TryExtractMaxYears(resumeText);
            if (years.HasValue)
            {
                return years.Value.ToString();
            }
        }

        if (Regex.IsMatch(normalizedQuestion, "linkedin", RegexOptions.IgnoreCase))
        {
            return TryExtractUrl(resumeText, "linkedin.com");
        }

        if (Regex.IsMatch(normalizedQuestion, "github", RegexOptions.IgnoreCase))
        {
            return TryExtractUrl(resumeText, "github.com");
        }

        if (optionHints.Count > 0)
        {
            var matched = optionHints.FirstOrDefault(option =>
                analysis.Keywords.Any(keyword => option.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(analysis.SuggestedJobTitle)
                    && option.Contains(analysis.SuggestedJobTitle, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(matched))
            {
                return matched;
            }
        }

        return null;
    }

    private static int? TryExtractMaxYears(string resumeText)
    {
        var matches = Regex.Matches(resumeText, @"\b(\d{1,2})\s*\+?\s*(years|year|yrs|yr)\b", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return null;
        }

        var values = matches
            .Select(m => m.Groups[1].Value)
            .Select(v => int.TryParse(v, out var years) ? years : -1)
            .Where(v => v >= 0)
            .ToList();

        return values.Count == 0 ? null : values.Max();
    }

    private static string? TryExtractUrl(string resumeText, string contains)
    {
        var matches = Regex.Matches(resumeText, @"https?://[^\s)]+", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            if (match.Value.Contains(contains, StringComparison.OrdinalIgnoreCase))
            {
                return match.Value.Trim();
            }
        }

        return null;
    }

    private static string CleanupModelAnswer(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return string.Empty;
        }

        var value = responseText.Trim();
        value = value.Replace("```", string.Empty, StringComparison.Ordinal);
        value = value.Trim('"', '\'', '`');
        value = Regex.Replace(value, "\\s+", " ").Trim();
        if (value.Length > 220)
        {
            value = value[..220].Trim();
        }

        return value;
    }

    private static string TruncateResumeText(string resumeText)
    {
        const int maxLength = 7000;
        if (resumeText.Length <= maxLength)
        {
            return resumeText;
        }

        return resumeText[..maxLength];
    }

    private static bool LooksLikeJobTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length < 4 || title.Length > 70)
        {
            return false;
        }

        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Length > 6)
        {
            return false;
        }

        if (!Regex.IsMatch(title, "[A-Za-z]"))
        {
            return false;
        }

        return RoleKeywords.Any(x => title.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsMalformedRoleNumber(string title)
    {
        return Regex.IsMatch(title, @"\b\d{2,}\s*(developer|engineer|architect|analyst|tester|consultant|programmer|manager|lead)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(title, @"\b\d{2,}(developer|engineer|architect|analyst|tester|consultant|programmer|manager|lead)\b", RegexOptions.IgnoreCase);
    }
}
