using Microsoft.AspNetCore.Http;

namespace JobAutoApply.Web.Services;

public interface IResumeTextExtractorService
{
    Task<string> ExtractTextAsync(IFormFile resumeFile, CancellationToken cancellationToken);
}
