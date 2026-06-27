using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

public interface IAnswerService
{
    
    Task<UploadAnswerResult> UploadAnswerAsync(
        Guid sessionId,
        Guid questionId,
        Guid candidateId,
        Stream audioStream,
        string contentType,
        int durationSec,
        CancellationToken ct = default);
    Task SaveResultAsync(
        Guid answerId,
        AnswerScoreCallbackRequest req,
        CancellationToken ct = default);

    // Worker báo chấm thất bại vĩnh viễn -> đánh dấu Failed để session thoát kẹt.
    Task MarkFailedAsync(
        Guid answerId,
        string? reason,
        CancellationToken ct = default);
}