using System.Text;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Isas.Shared.Files;

/// <summary>
/// DB17 — shared PdfPig-based text extractor. Lifted verbatim from the previously duplicated
/// <c>ParserService.ParseInternal</c> (Campaign) and <c>CVParserService.ParseInternal</c> (Interview):
/// buffer the stream (PdfPig needs full content), open, join each page's words, concatenate per page.
/// </summary>
public sealed class PdfTextExtractor : IPdfTextExtractor
{
    private readonly ILogger<PdfTextExtractor>? _logger;

    // Logger optional so the type is usable in unit tests / manual construction without DI.
    public PdfTextExtractor(ILogger<PdfTextExtractor>? logger = null) => _logger = logger;

    public Task<PdfExtractResult> ExtractAsync(Stream pdfStream, CancellationToken ct = default)
        => Task.Run(() => ExtractInternal(pdfStream), ct);

    private PdfExtractResult ExtractInternal(Stream pdfStream)
    {
        if (pdfStream.CanSeek)
            pdfStream.Position = 0;

        // Read into a byte array — PdfPig needs the full content.
        using var ms = new MemoryStream();
        pdfStream.CopyTo(ms);
        var bytes = ms.ToArray();

        using var document = PdfDocument.Open(bytes);

        var rawBuilder = new StringBuilder();
        var pageCount = 0;

        foreach (Page page in document.GetPages())
        {
            pageCount++;
            var pageText = string.Join(" ", page.GetWords().Select(w => w.Text));
            rawBuilder.AppendLine(pageText);
        }

        var rawText = rawBuilder.ToString();

        _logger?.LogDebug("PdfPig extracted {CharCount} chars from {Pages} pages.", rawText.Length, pageCount);

        return new PdfExtractResult(rawText, pageCount);
    }
}
