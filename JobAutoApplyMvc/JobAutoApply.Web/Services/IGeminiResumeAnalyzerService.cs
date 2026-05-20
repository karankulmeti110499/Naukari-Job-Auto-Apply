using JobAutoApply.Web.Models;

namespace JobAutoApply.Web.Services;

public interface IGeminiResumeAnalyzerService
{
    Task<ResumeAnalysisResult> AnalyzeAsync(string resumeText, CancellationToken cancellationToken);
}
