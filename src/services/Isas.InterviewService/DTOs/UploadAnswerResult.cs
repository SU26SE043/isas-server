namespace Isas.InterviewService.DTOs;

// Kết quả upload 1 câu trả lời. Các field phỏng vấn THÍCH ỨNG là OPTIONAL (default null/false) → client
// cũ không dùng vẫn chạy (backward-compat). Client mới đọc NextQuestion/InterviewComplete để hiện câu kế
// NGAY trong response upload (không cần poll GET session).
public record UploadAnswerResult(
    Guid AnswerId,
    Guid QuestionId,
    string Status,
    string? Transcript = null,                 // transcript đồng bộ (adaptive) — có thể hiện ngay cho ứng viên
    string? NextAction = null,                 // follow_up | clarify | new_question | end (adaptive; null nếu tắt)
    NextQuestionResponse? NextQuestion = null, // câu hỏi thích ứng vừa sinh; null khi end / adaptive tắt / không phải frontier
    bool InterviewComplete = false);           // adaptive: AI kết thúc / hết ngân sách → mời ứng viên submit

// Câu hỏi kế (adaptive) trả kèm response upload — cùng shape con của QuestionResponse (client render trực tiếp).
public record NextQuestionResponse(
    Guid Id,
    int OrderNo,
    string Content,
    int TimeLimitSec,
    string Kind);
