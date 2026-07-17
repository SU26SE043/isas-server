using Isas.CampaignService.Services;
using Isas.Shared.Files;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Isas.CampaignService.Tests;

/// <summary>
/// DB17 — Campaign ParserService delegates PDF text extraction to the shared IPdfTextExtractor
/// (Isas.Shared), keeping its raw-text-only ParseResult. PDF fixture generated via PdfPig's writer.
/// </summary>
public class ParserServiceDelegationTests
{
    private static byte[] BuildPdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(text, 12, new PdfPoint(25, 750), font);
        return builder.Build();
    }

    [Fact]
    public async Task Parser_DelegatesToSharedExtractor()
    {
        var bytes = BuildPdf("Campaign CV Text");
        var direct = await new PdfTextExtractor().ExtractAsync(new MemoryStream(bytes));

        var result = await new ParserService(new PdfTextExtractor()).ParseAsync(new MemoryStream(bytes));

        Assert.Equal(direct.RawText, result.RawText);
        Assert.Equal(1, result.PageCount);
        Assert.Contains("Campaign", result.RawText);
    }
}
