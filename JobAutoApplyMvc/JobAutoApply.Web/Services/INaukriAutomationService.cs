using JobAutoApply.Web.Models;

namespace JobAutoApply.Web.Services;

public interface INaukriAutomationService
{
    Task<IReadOnlyCollection<JobApplyRecord>> SearchAndApplyAsync(JobSearchRequest request, CancellationToken cancellationToken);
}
