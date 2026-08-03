using Isas.InterviewService.DTOs;
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

    // RAG grounding — citation ĐÃ RESOLVE cho câu hỏi này (chunkId model cite → sourceUrl/sourceTitle).
    // 3 TRẠNG THÁI (load-bearing, FE dựa vào — supervisor chốt):
    //   null       = câu KHÔNG đi qua đường grounding (session cũ / B2B / adaptive) → FE không nhãn.
    //   [] (rỗng)  = ĐÃ chạy grounding nhưng retrieval miss / AI không cite → `ungrounded`, FE nhãn nổi bật.
    //   non-empty  = grounded (chunk model THẬT SỰ cite trong tập đã cấp — drop id lạ, chống bịa nguồn).
    // ⇒ Đi qua grounding thì LUÔN set (ít nhất []), KHÔNG để null. jsonb nullable (không kèm content —
    // câu hỏi đã sinh, chỉ cần link nguồn để hiển thị).
    public List<Citation>? GroundingRefs { get; set; }

    // Phỏng vấn THÍCH ỨNG — nguồn câu hỏi (Seed = mở đầu; FollowUp/Clarify/NewQuestion = AI sinh sau
    // 1 câu trả lời). Mặc định Seed (rows cũ backfill Seed). Lưu string (GEN-2).
    public QuestionKind Kind { get; set; } = QuestionKind.Seed;

    // Phỏng vấn THÍCH ỨNG — answer đã "đẻ" ra câu hỏi này (null với seed). Vừa là provenance (soi cây
    // hội thoại) vừa là KHOÁ idempotency: unique filtered index ⇒ 1 answer sinh tối đa 1 câu kế (chống
    // re-upload / double-POST tạo trùng). Ref lỏng tới practice_answers (KHÔNG FK — tránh cascade path phụ).
    public Guid? GeneratedFromAnswerId { get; set; }

    // INT-17b — độ sâu trong CHUỖI đào sâu: 0 = câu gốc (seed), 1..N = câu AI đào sâu tầng thứ N.
    // Là khoá của trần "tối đa N câu sâu MỖI câu gốc" (`session.MaxDeepPerQuestion`): chỉ cần so
    // `Depth < trần` thay vì đếm ngược cả chuỗi. Row cũ backfill theo cây `generated_from_answer_id`.
    public int Depth { get; set; }

    // INT-17b — câu GỐC (seed) của chuỗi này; null ⇔ chính nó là gốc ⇒ gốc hiệu dụng = `RootQuestionId ?? Id`.
    // Dùng để (a) gom lịch sử theo ĐÚNG chuỗi khi hỏi AI câu kế, (b) soi cây hội thoại. Ref lỏng trong
    // cùng bảng, KHÔNG FK — cùng lý do `GeneratedFromAnswerId` ở trên (tránh cascade path phụ).
    public Guid? RootQuestionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation - mỗi câu hỏi có tối đa 1 answer (business rule)
    public PracticeAnswer? Answer { get; set; }
}