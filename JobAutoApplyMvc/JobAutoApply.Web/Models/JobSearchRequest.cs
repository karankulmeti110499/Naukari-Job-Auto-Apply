namespace JobAutoApply.Web.Models;

public sealed class JobSearchRequest
{
    public string JobTitle { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int MaxJobs { get; set; }
    public bool Headless { get; set; }
}
