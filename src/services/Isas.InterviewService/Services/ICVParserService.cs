using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services
{
    public interface ICVParserService
    {
        Task<CVParseResult> ParseAsync(Stream pdfStream, CancellationToken ct = default);
    }
}
