using Isas.InterviewService.Services;
using Isas.Shared.Files;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Isas.InterviewService.Tests;

/// <summary>
/// DB17 — the shared PdfPig text extractor (Isas.Shared) + CVParserService delegating to it while
/// keeping Interview's own structured CV extraction. PDF fixtures generated via PdfPig's writer (ASCII).
/// </summary>
public class SharedPdfExtractorTests
{
    private static byte[] BuildPdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var y = 750;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(25, y), font);
            y -= 20;
        }
        return builder.Build();
    }

    [Fact]
    public async Task Extractor_ReturnsTextAndPageCount()
    {
        var bytes = BuildPdf("Hello World");

        var result = await new PdfTextExtractor().ExtractAsync(new MemoryStream(bytes));

        Assert.Equal(1, result.PageCount);
        Assert.Contains("Hello", result.RawText);
        Assert.Contains("World", result.RawText);
    }

    [Fact]
    public async Task CVParser_DelegatesToSharedExtractor_SameRawText()
    {
        var bytes = BuildPdf("Hello World");
        var direct = await new PdfTextExtractor().ExtractAsync(new MemoryStream(bytes));

        var parsed = await new CVParserService(new PdfTextExtractor()).ParseAsync(new MemoryStream(bytes));

        // Delegation: CVParserService yields exactly the shared extractor's raw text + page count.
        Assert.Equal(direct.RawText, parsed.RawText);
        Assert.Equal(direct.PageCount, parsed.PageCount);
    }
}
