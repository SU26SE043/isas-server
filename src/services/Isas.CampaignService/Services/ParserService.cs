using Isas.CampaignService.DTOs;
using Isas.Shared.Files;

namespace Isas.CampaignService.Services
{
    // DB17: PDF text extraction now lives in Isas.Shared (IPdfTextExtractor) — Campaign keeps its own
    // raw-text-only DTO (ParseResult); the byte-identical PdfPig loop moved to the shared extractor.
    public class ParserService : IParserService
    {
        private readonly IPdfTextExtractor _extractor;

        public ParserService(IPdfTextExtractor extractor)
        {
            _extractor = extractor;
        }

        public async Task<ParseResult> ParseAsync(Stream pdfStream, CancellationToken ct = default)
        {
            var extracted = await _extractor.ExtractAsync(pdfStream, ct);
            return new ParseResult
            {
                RawText = extracted.RawText,
                PageCount = extracted.PageCount,
            };
        }
    }
}
