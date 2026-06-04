namespace Isas.InterviewService.Services;

public interface ITranscriptionService
{
    Task<string> TranscribeAsync(Guid audioFileId, CancellationToken ct = default);
}