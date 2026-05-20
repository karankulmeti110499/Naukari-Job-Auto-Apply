using ClosedXML.Excel;
using JobAutoApply.Web.Models;
using JobAutoApply.Web.Options;
using Microsoft.Extensions.Options;

namespace JobAutoApply.Web.Services;

public sealed class ExcelJobDatabaseService : IExcelJobDatabaseService
{
    private const string WorksheetName = "Applications";
    private const string RunWorksheetName = "AppliedThisRun";

    private readonly NaukriOptions _naukriOptions;

    public ExcelJobDatabaseService(IOptions<NaukriOptions> naukriOptions)
    {
        _naukriOptions = naukriOptions.Value;
    }

    public Task SaveRecordsAsync(IReadOnlyCollection<JobApplyRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return Task.CompletedTask;
        }

        var filePath = Path.GetFullPath(_naukriOptions.ExcelFilePath);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var workbook = File.Exists(filePath) ? new XLWorkbook(filePath) : new XLWorkbook();
        var worksheet = workbook.Worksheets.FirstOrDefault(x => x.Name == WorksheetName) ?? workbook.AddWorksheet(WorksheetName);
        EnsureHeader(worksheet);

        var rowNumber = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            rowNumber++;
            worksheet.Cell(rowNumber, 1).Value = record.CapturedAt.LocalDateTime;
            worksheet.Cell(rowNumber, 2).Value = record.JobTitle;
            worksheet.Cell(rowNumber, 3).Value = record.Company;
            worksheet.Cell(rowNumber, 4).Value = record.Location;
            worksheet.Cell(rowNumber, 5).Value = record.JobUrl;
            worksheet.Cell(rowNumber, 6).Value = record.ApplyType;
            worksheet.Cell(rowNumber, 7).Value = record.Status;
            worksheet.Cell(rowNumber, 8).Value = record.Notes;

            if (!string.IsNullOrWhiteSpace(record.JobUrl))
            {
                worksheet.Cell(rowNumber, 5).SetHyperlink(new XLHyperlink(record.JobUrl));
            }

            if (record.ApplyType.Contains("company site", StringComparison.OrdinalIgnoreCase))
            {
                var row = worksheet.Row(rowNumber);
                row.Style.Fill.BackgroundColor = XLColor.LightYellow;
                row.Style.Font.Bold = true;
            }
        }

        worksheet.Columns(1, 8).AdjustToContents();
        workbook.SaveAs(filePath);

        return Task.CompletedTask;
    }

    public Task<string> SaveRunAppliedRecordsAsync(IReadOnlyCollection<JobApplyRecord> records, CancellationToken cancellationToken)
    {
        var appliedRecords = records
            .Where(IsAppliedRecord)
            .ToList();
        var companySiteRecords = records
            .Where(IsCompanySiteRecord)
            .ToList();

        if (appliedRecords.Count == 0 && companySiteRecords.Count == 0)
        {
            return Task.FromResult(string.Empty);
        }

        var runDirectory = GetRunDownloadsDirectory();
        Directory.CreateDirectory(runDirectory);

        var fileName = $"AppliedJobs-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
        var filePath = Path.Combine(runDirectory, fileName);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet(RunWorksheetName);
        EnsureHeader(worksheet);

        var rowNumber = 1;
        foreach (var record in appliedRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();

            rowNumber = WriteRecord(worksheet, rowNumber, record);
        }

        if (companySiteRecords.Count > 0)
        {
            if (rowNumber > 1)
            {
                rowNumber++;
            }

            worksheet.Cell(rowNumber, 1).Value = "Apply on company site (manual)";
            var sectionHeader = worksheet.Range(rowNumber, 1, rowNumber, 8);
            sectionHeader.Merge();
            sectionHeader.Style.Font.Bold = true;
            sectionHeader.Style.Fill.BackgroundColor = XLColor.LightYellow;

            foreach (var record in companySiteRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowNumber = WriteRecord(worksheet, rowNumber, record);
                var row = worksheet.Row(rowNumber);
                row.Style.Fill.BackgroundColor = XLColor.LightYellow;
            }
        }

        worksheet.Columns(1, 8).AdjustToContents();
        workbook.SaveAs(filePath);

        return Task.FromResult(fileName);
    }

    private static void EnsureHeader(IXLWorksheet worksheet)
    {
        if (!worksheet.Cell(1, 1).IsEmpty())
        {
            return;
        }

        worksheet.Cell(1, 1).Value = "CapturedAt";
        worksheet.Cell(1, 2).Value = "JobTitle";
        worksheet.Cell(1, 3).Value = "Company";
        worksheet.Cell(1, 4).Value = "Location";
        worksheet.Cell(1, 5).Value = "JobUrl";
        worksheet.Cell(1, 6).Value = "ApplyType";
        worksheet.Cell(1, 7).Value = "Status";
        worksheet.Cell(1, 8).Value = "Notes";

        var headerRange = worksheet.Range(1, 1, 1, 8);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.AliceBlue;
    }

    private string GetRunDownloadsDirectory()
    {
        var databasePath = Path.GetFullPath(_naukriOptions.ExcelFilePath);
        var baseDirectory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.GetFullPath("Data");
        }

        return Path.Combine(baseDirectory, "RunDownloads");
    }

    private static bool IsAppliedRecord(JobApplyRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ApplyType) || string.IsNullOrWhiteSpace(record.Status))
        {
            return false;
        }

        if (!record.ApplyType.Equals("Apply", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return record.Status.Contains("confirmed", StringComparison.OrdinalIgnoreCase)
            || record.Status.Contains("submitted", StringComparison.OrdinalIgnoreCase)
            || record.Status.Contains("success", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompanySiteRecord(JobApplyRecord record)
    {
        return record.ApplyType.Contains("company site", StringComparison.OrdinalIgnoreCase);
    }

    private static int WriteRecord(IXLWorksheet worksheet, int rowNumber, JobApplyRecord record)
    {
        var nextRow = rowNumber + 1;
        worksheet.Cell(nextRow, 1).Value = record.CapturedAt.LocalDateTime;
        worksheet.Cell(nextRow, 2).Value = record.JobTitle;
        worksheet.Cell(nextRow, 3).Value = record.Company;
        worksheet.Cell(nextRow, 4).Value = record.Location;
        worksheet.Cell(nextRow, 5).Value = record.JobUrl;
        worksheet.Cell(nextRow, 6).Value = record.ApplyType;
        worksheet.Cell(nextRow, 7).Value = record.Status;
        worksheet.Cell(nextRow, 8).Value = record.Notes;

        if (!string.IsNullOrWhiteSpace(record.JobUrl))
        {
            worksheet.Cell(nextRow, 5).SetHyperlink(new XLHyperlink(record.JobUrl));
        }

        return nextRow;
    }
}
