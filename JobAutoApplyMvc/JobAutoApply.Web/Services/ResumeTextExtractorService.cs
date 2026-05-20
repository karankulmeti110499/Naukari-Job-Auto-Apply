using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Http;
using UglyToad.PdfPig;

namespace JobAutoApply.Web.Services;

public sealed class ResumeTextExtractorService : IResumeTextExtractorService
{
    public async Task<string> ExtractTextAsync(IFormFile resumeFile, CancellationToken cancellationToken)
    {
        if (resumeFile is null || resumeFile.Length == 0)
        {
            throw new InvalidOperationException("Please upload a non-empty resume file.");
        }

        await using var memoryStream = new MemoryStream();
        await resumeFile.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        var extension = Path.GetExtension(resumeFile.FileName).ToLowerInvariant();
        return extension switch
        {
            ".txt" => await ExtractFromTxtAsync(memoryStream, cancellationToken),
            ".docx" => ExtractFromDocx(memoryStream),
            ".pdf" => ExtractFromPdf(memoryStream),
            _ => throw new NotSupportedException("Unsupported resume type. Use .pdf, .docx, or .txt"),
        };
    }

    private static async Task<string> ExtractFromTxtAsync(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string ExtractFromDocx(Stream stream)
    {
        stream.Position = 0;
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var textNodes = body.Descendants<Text>();
        return string.Join(' ', textNodes.Select(x => x.Text));
    }

    private static string ExtractFromPdf(Stream stream)
    {
        stream.Position = 0;
        using var pdf = PdfDocument.Open(stream);
        var pagesText = pdf.GetPages().Select(x => x.Text);
        return string.Join(Environment.NewLine, pagesText);
    }
}
