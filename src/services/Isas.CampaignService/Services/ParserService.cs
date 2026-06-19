using Isas.CampaignService.DTOs;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Isas.CampaignService.Services
{
    public class ParserService : IParserService
    {
        private ILogger<ParserService> _logger;

        public ParserService(ILogger<ParserService> logger)
        {
            _logger = logger;
        }

        public async Task<ParseResult> ParseAsync(Stream pdfStream, CancellationToken ct = default)
        {
            return await Task.Run(() => ParseInternal(pdfStream), ct);
        }

        private ParseResult ParseInternal(Stream pdfStream)
        {
            if (pdfStream.CanSeek)
                pdfStream.Position = 0;

            // Read into byte array — PdfPig needs full content
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

            _logger.LogDebug("PdfPig extracted {CharCount} chars from {Pages} pages.", rawText.Length, pageCount);

            return new ParseResult
            {
                RawText = rawText,
                PageCount = pageCount,
            };
        }
    }
}