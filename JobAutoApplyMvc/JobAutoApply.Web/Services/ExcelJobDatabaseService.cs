using ClosedXML.Excel;
using JobAutoApply.Web.Models;
using JobAutoApply.Web.Options;
using Microsoft.Extensions.Options;

namespace JobAutoApply.Web.Services;

public sealed class ExcelJobDatabaseService : IExcelJobDatabaseService
{
    private const string WorksheetName = "Applications";

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
}
