using JobAutoApply.Web.Models;
using Microsoft.AspNetCore.Http;

namespace JobAutoApply.Web.ViewModels;

public sealed class JobAutomationViewModel
{
    public IFormFile? ResumeFile { get; set; }
    public string? JobTitleOverride { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int MaxJobs { get; set; } = 10;
    public bool Headless { get; set; }
    public string? ParsedJobTitle { get; set; }
    public IReadOnlyCollection<string> Keywords { get; set; } = [];
    public IReadOnlyCollection<JobApplyRecord> Results { get; set; } = [];
    public string? StatusMessage { get; set; }
    public bool IsError { get; set; }
}
