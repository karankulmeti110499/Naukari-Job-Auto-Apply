namespace JobAutoApply.Web.Options;

public sealed class NaukriOptions
{
    public const string SectionName = "Naukri";

    public string BaseUrl { get; set; } = "https://www.naukri.com";
    public int DefaultMaxJobs { get; set; } = 10;
    public bool HeadlessByDefault { get; set; } = false;
    public string ExcelFilePath { get; set; } = "Data/JobApplications.xlsx";
    public string StorageStateFilePath { get; set; } = "Data/NaukriStorageState.json";
}
