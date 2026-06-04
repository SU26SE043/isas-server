using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services;

public interface IPracticeService
{
    // Tạo phiên (state = draft)
    Task<PracticeSessionResponse> CreateSessionAsync(
        Guid userId, CreatePracticeSessionRequest request, CancellationToken ct = default);

    // Sinh câu hỏi qua AIService → lưu, state = in_progress
    Task<PracticeSessionResponse> GenerateQuestionsAsync(
        Guid userId, Guid sessionId, CancellationToken ct = default);

    // Trả lời 1 câu
    Task<AnswerResponse> SubmitAnswerAsync(
        Guid userId, Guid sessionId, SubmitAnswerRequest request, CancellationToken ct = default);

    // Submit toàn phiên → đẩy RabbitMQ chấm async, state = submitted
    Task SubmitSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default);

    // Xem chi tiết 1 phiên (kèm câu hỏi + đáp án)
    Task<PracticeSessionResponse?> GetSessionAsync(
        Guid userId, Guid sessionId, CancellationToken ct = default);

    // Lịch sử
    Task<IReadOnlyList<PracticeSessionSummary>> GetHistoryAsync(
        Guid userId, CancellationToken ct = default);
}
