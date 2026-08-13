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

    // Tiêu chí NỘI DUNG mà câu hỏi này thực sự nhắm tới (AIService gắn nhãn lúc sinh câu hỏi).
    // Quyết định bộ tiêu chí gửi vào lượt chấm: 4 tiêu chí CÁCH NÓI luôn có mặt, cộng đúng những
    // tiêu chí nội dung liệt kê ở đây (xem ScoringScopeFilter).
    //
    // 🔑 3 TRẠNG THÁI (load-bearing — `[]` KHÁC `null`, tầng lưu trữ TUYỆT ĐỐI không được quy `[]` về null):
    //   null      = CHƯA HỎI / không có nhãn → LÙI AN TOÀN, chấm đủ cả rubric như trước. Phủ: câu của
    //               buổi cũ, câu B2B (Campaign không gửi tiêu chí), câu thích ứng do /decide-next sinh,
    //               và ca AIService trả nhãn toàn id lạ (không đủ tin để thu hẹp).
    //   [] (rỗng) = ĐÃ HỎI, AIService kết luận câu này KHÔNG nhắm tiêu chí nội dung nào ("giới thiệu
    //               bản thân", "vì sao bạn ứng tuyển") ⇒ chỉ chấm 4 tiêu chí CÁCH NÓI.
    //   non-empty = tiêu chí nội dung được nhắm tới (+ 4 tiêu chí cách nói).
    //
    // ⚠ Gộp `[]` vào null làm tính năng NO-OP đúng ở nhóm câu cần nó nhất: câu xã giao vẫn bị chấm
    // "Thiết kế hệ thống & CSDL" — chính là hình dạng lỗi gốc mà cả thay đổi này sinh ra để diệt.
    // jsonb nullable ⇒ AddColumn không cần defaultValue (né bug jsonb-rỗng-default của F15).
    public List<Guid>? TargetCriterionIds { get; set; }

    // Phỏng vấn THÍCH ỨNG — nguồn câu hỏi (Seed = mở đầu; FollowUp/Clarify/NewQuestion = AI sinh sau
    // 1 câu trả lời). Mặc định Seed (rows cũ backfill Seed). Lưu string (GEN-2).
    /// <summary>
    /// SNAPSHOT đáp án mẫu HR soạn cho câu này (B2B). null = không có (câu B2C, câu đào sâu AI sinh
    /// lúc thi, hoặc chiến dịch chưa soạn đáp án).
    ///
    /// <para><b>Vì sao snapshot chứ không đọc live từ CampaignService lúc chấm:</b> nó là một phần của
    /// THƯỚC ĐO. Đọc live thì hai ứng viên cùng chiến dịch có thể bị chấm theo hai bản đáp án khác nhau
    /// nếu ai đó sửa ở giữa, trong khi điểm vẫn đem xếp hạng chung (CAMP-10). Snapshot cũng giữ cho việc
    /// chấm không phụ thuộc một service khác còn sống hay không.</para>
    ///
    /// <para>Câu ĐÀO SÂU do AI sinh lúc thi không có đáp án mẫu — không ai soạn trước cho chúng. Đó là
    /// lý do prompt phải nói rõ đây là "một đáp án tốt để tham khảo", không phải đáp án duy nhất đúng:
    /// nếu không, ứng viên diễn đạt khác mà vẫn đúng sẽ bị trừ điểm ở câu có đáp án, còn câu không có
    /// đáp án thì không — hai thước trong cùng một buổi.</para>
    /// </summary>
    public string? SampleAnswer { get; set; }

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