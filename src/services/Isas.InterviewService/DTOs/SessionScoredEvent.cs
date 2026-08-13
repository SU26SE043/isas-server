namespace Isas.InterviewService.DTOs;

// Message phát lên RabbitMQ khi session đóng sang Scored (E2 — interview.md §Sự kiện phát ra).
// Người nghe: Campaign (upsert campaign_rankings, B2B) + Payment (consume credit, B2B & B2C).
// B2C: CampaignId = null — Campaign bỏ qua (không campaign_id để ranking), Payment vẫn xử lý.
public class SessionScoredEvent
{
    public Guid SessionId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid CandidateId { get; set; }

    // Điểm tổng có trọng số: Σ(% tiêu chí đã chấm × weight) / Σweight, kẹp [0,100].
    // Công thức khớp campaign.md §campaign_rankings ("total_score = Σ pct×weight, chuẩn hoá
    // chia Σweight — Interview tính"). Snapshot phát kèm event — KHÔNG ghi vào DB Interview
    // (khác practice_sessions.overall_score của BC9, vốn dùng trung bình cộng equal-weight
    // cho B2C và CHƯA build).
    public decimal TotalScore { get; set; }

    public DateTime ScoredAt { get; set; }

    // B2B — phiên bản bộ tiêu chí buổi này bị chấm bằng (practice_sessions.campaign_rubric_version).
    // Bảng xếp hạng PHẢI gắn được nhãn này: HR sửa mốc giữa chừng là đổi THƯỚC ĐO, mà CAMP-10 đem
    // điểm của mọi ứng viên trong campaign so thẳng với nhau. Cùng lý do đã sinh ra
    // scoring_scope_version (rules.md INT-18) — đổi mốc còn đổi thước mạnh hơn đổi phạm vi.
    //
    // Nullable + thêm ở CUỐI ⇒ bản Campaign cũ đọc event mới không vỡ, và event cũ đang nằm trong
    // outbox (chưa gửi lúc deploy) deserialize ra null thay vì nổ.
    // ⚠ null nghĩa là "KHÔNG BIẾT" — B2C, hoặc buổi có trước cột ghim. Đừng vẽ null thành v1 (BK23).
    public int? RubricVersion { get; set; }
}
