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
