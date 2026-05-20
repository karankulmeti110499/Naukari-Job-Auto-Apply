using JobAutoApply.Web.Models;

namespace JobAutoApply.Web.Services;

public interface IGeminiResumeAnalyzerService
{
    Task<ResumeAnalysisResult> AnalyzeAsync(string resumeText, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<string>> RecommendJobTitlesAsync(string resumeText, ResumeAnalysisResult analysis, CancellationToken cancellationToken);
    Task<string?> SuggestAnswerForApplicationQuestionAsync(string question, string resumeText, ResumeAnalysisResult analysis, IReadOnlyCollection<string> optionHints, CancellationToken cancellationToken);
}
