namespace Isas.InterviewService.DTOs;

// Phỏng vấn THÍCH ỨNG — hợp đồng giữa InterviewService và AIService /decide-next (typed HttpClient).

// 1 lượt Q&A trước đó (stateless — Interview gửi kèm lịch sử; AIService không giữ state, GEN-4).
public record DecideTurnDto(string Question, string? Answer, string Kind);

// Tiêu chí năng lực để AI NEO câu hỏi thích ứng về cùng rubric (giữ công bằng chấm/ranking B2B).
public record DecideCriterionDto(string Name, string? Description);

// Kết quả decide-next: action + câu hỏi kế (null nếu end) + transcript đã transcribe đồng bộ.
public record DecideNextResult(
    string Action,          // follow_up | clarify | new_question | end
    string? NextQuestion,   // null ⇔ end
    string? Transcript,     // transcript AIService trả về (single-source; đẩy vào ScoringJob)
    string? Reason);
