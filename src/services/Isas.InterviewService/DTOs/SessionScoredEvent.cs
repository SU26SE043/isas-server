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
}
