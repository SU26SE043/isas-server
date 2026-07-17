using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

public class PracticeSession : IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();
 
    // Tham chiếu lỏng sang AuthService (candidate) - không FK xuyên service
    public Guid CandidateId { get; set; }

    // Phân biệt B2B/B2C: null = B2C luyện tập; có giá trị = bài thi của 1 campaign.
    // Ref lỏng sang CampaignService - KHÔNG FK xuyên service (architecture §5).
    public Guid? CampaignId { get; set; }

    // FK cứng tới FileRecord (B2C: file_records nằm chung interview DB)
    public Guid? CvId { get; set; }
    public Guid? JdId { get; set; }
    public JobCategory JobCategory { get; set; }
 
    public SessionStatus Status { get; set; } = SessionStatus.GeneratingQuestions;
 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // DB14 — audit: đóng dấu mỗi lần session bị sửa (status flip, chấm xong, abandon...). C# init giống
    // CreatedAt (Interview CreatedAt do C# gán, không dùng DB now()) để insert SQLite/EnsureCreated chạy;
    // config cũng đặt default now() ở DB. Stamp tự động khi Modified (SaveChanges override); flip qua
    // ExecuteUpdate (SessionAbandonSweeper, SessionScoringNotifier) tự .SetProperty(UpdatedAt).
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // I2 (D21) — hạn chót NHẬN BÀI của cả buổi (KHÔNG phải giới hạn tổng thời gian làm bài):
    // B2B = campaigns.expires_at (Campaign gửi kèm lúc tạo session); B2C = null (không hard-deadline,
    // chỉ giới hạn từng câu qua PracticeQuestion.TimeLimitSec). Quá Deadline + InProgress →
    // SessionAbandonSweeper auto-submit (≥1 answer) hoặc SessionAbandoned (0 answer) — chống reservation treo.
    public DateTime? Deadline { get; set; }

    // BC9 — tổng kết buổi luyện B2C, set khi Scored (campaign_id null); null khi chưa chấm xong / B2B.
    public decimal? OverallScore { get; set; }   // điểm tổng 0–100 (trung bình cộng pct các tiêu chí)
    public int? AnsweredCount { get; set; }        // số câu đã chấm lúc tính kết quả (snapshot)

    // BC10 — nhận xét chung buổi (AI sinh best-effort khi Scored, chỉ B2C); null nếu chưa/AI lỗi/B2B.
    public string? OverallComment { get; set; }

    // Navigation
    public ICollection<PracticeQuestion> Questions { get; set; } = [];
    public ICollection<PracticeAnswer> Answers { get; set; } = [];
    public ICollection<SessionCriterionScore> CriterionScores { get; set; } = [];   // BC9 (B2C)
}
