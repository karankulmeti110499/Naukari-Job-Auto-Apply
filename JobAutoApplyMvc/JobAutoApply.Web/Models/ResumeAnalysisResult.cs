namespace JobAutoApply.Web.Models;

public sealed class ResumeAnalysisResult
{
    public string SuggestedJobTitle { get; set; } = string.Empty;
    public List<string> Keywords { get; set; } = [];
}
