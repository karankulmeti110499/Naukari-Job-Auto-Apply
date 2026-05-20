using JobAutoApply.Web.Models;
using JobAutoApply.Web.Options;
using JobAutoApply.Web.Services;
using JobAutoApply.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace JobAutoApply.Web.Controllers;

public sealed class JobsController : Controller
{
    private readonly IResumeTextExtractorService _resumeTextExtractorService;
    private readonly IGeminiResumeAnalyzerService _geminiResumeAnalyzerService;
    private readonly INaukriAutomationService _naukriAutomationService;
    private readonly IExcelJobDatabaseService _excelJobDatabaseService;
    private readonly NaukriOptions _naukriOptions;

    public JobsController(
        IResumeTextExtractorService resumeTextExtractorService,
        IGeminiResumeAnalyzerService geminiResumeAnalyzerService,
        INaukriAutomationService naukriAutomationService,
        IExcelJobDatabaseService excelJobDatabaseService,
        IOptions<NaukriOptions> naukriOptions)
    {
        _resumeTextExtractorService = resumeTextExtractorService;
        _geminiResumeAnalyzerService = geminiResumeAnalyzerService;
        _naukriAutomationService = naukriAutomationService;
        _excelJobDatabaseService = excelJobDatabaseService;
        _naukriOptions = naukriOptions.Value;
    }

    [HttpGet]
    public IActionResult Index()
    {
        return View(new JobAutomationViewModel
        {
            MaxJobs = _naukriOptions.DefaultMaxJobs,
            Headless = _naukriOptions.HeadlessByDefault,
        });
    }

    [HttpGet]
    public IActionResult DownloadExcel()
    {
        var excelPath = Path.GetFullPath(_naukriOptions.ExcelFilePath);
        if (!System.IO.File.Exists(excelPath))
        {
            return NotFound("Excel database file not found.");
        }

        var fileName = Path.GetFileName(excelPath);
        var contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        var bytes = System.IO.File.ReadAllBytes(excelPath);
        return File(bytes, contentType, fileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AnalyzeResume(IFormFile? resumeFile, CancellationToken cancellationToken)
    {
        if (resumeFile is null || resumeFile.Length == 0)
        {
            return BadRequest(new
            {
                success = false,
                message = "Please upload a valid resume file.",
            });
        }

        try
        {
            var resumeText = await _resumeTextExtractorService.ExtractTextAsync(resumeFile, cancellationToken);
            var analysis = await _geminiResumeAnalyzerService.AnalyzeAsync(resumeText, cancellationToken);

            return Ok(new
            {
                success = true,
                previewText = BuildPreviewText(resumeText),
                suggestedJobTitle = analysis.SuggestedJobTitle,
                keywords = analysis.Keywords,
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                success = false,
                message = ex.Message,
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(JobAutomationViewModel model, CancellationToken cancellationToken)
    {
        model.MaxJobs = model.MaxJobs <= 0 ? _naukriOptions.DefaultMaxJobs : model.MaxJobs;

        if (model.ResumeFile is null)
        {
            model.IsError = true;
            model.StatusMessage = "Please upload your resume first.";
            return View("Index", model);
        }

        try
        {
            var resumeText = await _resumeTextExtractorService.ExtractTextAsync(model.ResumeFile, cancellationToken);
            var analysis = await _geminiResumeAnalyzerService.AnalyzeAsync(resumeText, cancellationToken);

            var resolvedJobTitle = string.IsNullOrWhiteSpace(model.JobTitleOverride)
                ? analysis.SuggestedJobTitle
                : model.JobTitleOverride.Trim();
            resolvedJobTitle = SanitizeSearchTitle(resolvedJobTitle);

            if (string.IsNullOrWhiteSpace(resolvedJobTitle))
            {
                model.IsError = true;
                model.StatusMessage = "Could not determine a job title from resume. Please provide a job title override.";
                model.Keywords = analysis.Keywords;
                return View("Index", model);
            }

            var request = new JobSearchRequest
            {
                JobTitle = resolvedJobTitle,
                Email = model.Email.Trim(),
                Password = model.Password,
                MaxJobs = model.MaxJobs,
                Headless = model.Headless,
            };

            var results = await _naukriAutomationService.SearchAndApplyAsync(request, cancellationToken);
            await _excelJobDatabaseService.SaveRecordsAsync(results, cancellationToken);
            TempData["AutoDownloadExcel"] = true;

            model.ParsedJobTitle = resolvedJobTitle;
            model.Keywords = analysis.Keywords;
            model.Results = results;
            if (results.Count == 0)
            {
                model.StatusMessage = "No jobs were captured from Naukri. Please try a broader title, disable filters on Naukri, and rerun in non-headless mode.";
                model.IsError = true;
            }
            else
            {
                model.StatusMessage = $"Completed. Processed {results.Count} jobs and updated Excel database.";
                model.IsError = false;
            }
        }
        catch (Exception ex)
        {
            model.IsError = true;
            model.StatusMessage = ex.Message;
        }

        return View("Index", model);
    }

    private static string BuildPreviewText(string resumeText)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            return string.Empty;
        }

        const int maxPreviewLength = 1800;
        var normalized = resumeText.Replace("\r", string.Empty).Trim();
        if (normalized.Length <= maxPreviewLength)
        {
            return normalized;
        }

        return normalized[..maxPreviewLength] + "...";
    }

    private static string SanitizeSearchTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var value = Regex.Replace(title, "\\s+", " ").Trim();
        value = Regex.Replace(value, @"\b\d{2,}(developer|engineer|architect|analyst|tester|consultant|programmer|manager|lead)\b", "$1", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\b\d{2,}\s+(?=(developer|engineer|architect|analyst|tester|consultant|programmer|manager|lead)\b)", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bdot\s*net\b", ".NET", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\basp\.?\s*net\b", "ASP.NET", RegexOptions.IgnoreCase);
        return Regex.Replace(value, "\\s+", " ").Trim();
    }
}
