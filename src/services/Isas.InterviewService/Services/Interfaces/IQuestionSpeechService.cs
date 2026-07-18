using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// TTS đọc câu hỏi — kiểm quyền (chỉ CHỦ buổi, INT-11) rồi lấy audio câu hỏi.
// Áp cho CẢ B2C lẫn B2B: session B2B cũng là practice_sessions và cũng có candidate_id,
// nên một endpoint phục vụ cả hai dòng.
public interface IQuestionSpeechService
{
    /// <summary>
    /// Trả audio đọc câu hỏi. null ⇔ session không tồn tại HOẶC câu hỏi không thuộc session đó
    /// (controller map 404). Không phải chủ buổi → UnauthorizedAccessException (403).
    /// AIService lỗi → AiServiceException (502).
    /// </summary>
    Task<QuestionSpeech?> GetQuestionSpeechAsync(
        Guid candidateId, Guid sessionId, Guid questionId, CancellationToken ct = default);
}
