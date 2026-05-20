namespace JobAutoApply.Web.Models;

public sealed class JobApplyRecord
{
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public string JobTitle { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string JobUrl { get; set; } = string.Empty;
    public string ApplyType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
