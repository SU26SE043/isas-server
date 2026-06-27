using Isas.CampaignService.DTOs;

namespace Isas.CampaignService.Services
{
    public interface IParserService
    {
        Task<ParseResult> ParseAsync(Stream pdfStream, CancellationToken ct = default);
    }
}
