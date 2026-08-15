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
    // `noSpeech` = bản ghi KHÔNG có tiếng nói (VAD) hoặc bản chép là rác máy sinh → Skipped thay vì
    // Failed (khác NHÃN, không khác luật tiền — xem AnswerService.MarkFailedAsync).
    Task MarkFailedAsync(
        Guid answerId,
        string? reason,
        bool noSpeech = false,
        CancellationToken ct = default);

    // Chốt sổ cưỡng bức buổi kẹt `Scoring` quá lâu (SessionAbandonSweeper gọi). Trả true nếu có đụng.
    Task<bool> FinalizeStuckSessionAsync(
        Guid sessionId,
        CancellationToken ct = default);
}