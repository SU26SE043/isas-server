using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

public class PracticeQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }
    public PracticeSession Session { get; set; } = null!;

    public int OrderNo { get; set; }
    public string Content { get; set; } = null!;
    public int TimeLimitSec { get; set; }

    // Phỏng vấn THÍCH ỨNG — nguồn câu hỏi (Seed = mở đầu; FollowUp/Clarify/NewQuestion = AI sinh sau
    // 1 câu trả lời). Mặc định Seed (rows cũ backfill Seed). Lưu string (GEN-2).
    public QuestionKind Kind { get; set; } = QuestionKind.Seed;

    // Phỏng vấn THÍCH ỨNG — answer đã "đẻ" ra câu hỏi này (null với seed). Vừa là provenance (soi cây
    // hội thoại) vừa là KHOÁ idempotency: unique filtered index ⇒ 1 answer sinh tối đa 1 câu kế (chống
    // re-upload / double-POST tạo trùng). Ref lỏng tới practice_answers (KHÔNG FK — tránh cascade path phụ).
    public Guid? GeneratedFromAnswerId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation - mỗi câu hỏi có tối đa 1 answer (business rule)
    public PracticeAnswer? Answer { get; set; }
}