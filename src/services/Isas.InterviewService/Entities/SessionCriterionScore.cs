namespace Isas.InterviewService.Entities;

// BC9 — breakdown điểm mỗi tiêu chí của 1 buổi luyện B2C, ghi khi session -> Scored.
// Điểm tổng buổi nằm ở practice_sessions.overall_score; bảng này giữ chi tiết từng tiêu chí.
// CHỈ B2C (campaign_id null); B2B không ghi (ranking tính ở CampaignService từ event SessionScored).
public class SessionCriterionScore
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }
    public PracticeSession Session { get; set; } = null!;

    // Ref tới rubric_criteria (FK Restrict) — giữ điểm lịch sử, chặn xoá tiêu chí đang được tham chiếu.
    public Guid CriterionId { get; set; }

    // Snapshot lúc tính (rubric có thể đổi version sau).
    public string CriterionName { get; set; } = null!;

    // Điểm TB tiêu chí qua các câu đã chấm (thang riêng từng tiêu chí).
    public decimal AverageScore { get; set; }

    // Snapshot maxScore của tiêu chí.
    public int MaxScore { get; set; }

    // average_score / max_score × 100 (0–100).
    public decimal Percentage { get; set; }

    // Snapshot weight — B2C KHÔNG dùng cho overall (trung bình cộng); chỉ hiển thị.
    public decimal Weight { get; set; }

    // percentage < ngưỡng (mặc định 50%) — tiêu chí yếu cần ưu tiên cải thiện.
    public bool NeedsImprovement { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
