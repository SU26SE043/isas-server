using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

// BC12 (D20) — roadmap ôn tập cá nhân hoá B2C. AIService sinh cấu trúc milestone/lesson (sync),
// InterviewService LƯU ở đây (AI KHÔNG ghi DB — GEN-4). Tạo roadmap KHÔNG trừ credit; chỉ session
// luyện bên trong (BC14) mới reserve→consume (D7/D15). Điểm yếu + baseline lấy từ session_criterion_scores
// (BC9) của các buổi B2C đã Scored gần nhất; CV (nếu có) đọc parsed_text file_records.
public class Roadmap
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Ref lỏng sang AuthService (chủ sở hữu) — không FK xuyên service. Lịch sử chỉ của chính user (BC-3).
    public Guid CandidateId { get; set; }

    // BE-6 — tên hiển thị. NULL ở tầng DB để hàng tạo trước BE-6 không phải backfill; đường ĐỌC
    // luôn suy ra tên dùng được (RoadmapNaming.Resolve) nên null không bao giờ chảy ra API.
    public string? Name { get; set; }

    public JobCategory JobCategory { get; set; }
    public RoadmapLevel Level { get; set; }
    public string Language { get; set; } = "vi";

    // FK Restrict → file_records (cùng DB interview) — đồng bộ cách PracticeSession.CvId cấu hình.
    public Guid? CvId { get; set; }

    // jsonb? — snapshot lúc tạo: session Scored dùng làm input điểm yếu.
    public List<Guid>? SourceSessionIds { get; set; }

    // jsonb? — { criterionName: pct } mốc % per tiêu chí lúc tạo (so cải thiện); null nếu chưa có buổi Scored.
    public Dictionary<string, decimal>? Baseline { get; set; }

    public RoadmapStatus Status { get; set; } = RoadmapStatus.Active;

    // jsonb? — snapshot RoadmapReport khi Completed (BC15). BC12 không set.
    public string? FinalReport { get; set; }

    // Nhận xét chung roadmap — AI /summarize-roadmap best-effort (BC15). BC12 không set.
    public string? OverallComment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // Navigation — Cascade theo roadmap_id.
    public ICollection<RoadmapMilestone> Milestones { get; set; } = [];
}
