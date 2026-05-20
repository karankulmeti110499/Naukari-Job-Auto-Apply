using JobAutoApply.Web.Models;

namespace JobAutoApply.Web.Services;

public interface IExcelJobDatabaseService
{
    Task SaveRecordsAsync(IReadOnlyCollection<JobApplyRecord> records, CancellationToken cancellationToken);
}
