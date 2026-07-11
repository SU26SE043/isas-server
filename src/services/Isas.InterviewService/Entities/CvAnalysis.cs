using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

// BC7 (B2C) — kết quả phân tích CV: InterviewService gọi AIService `/analyze-cv` (sync)
// rồi LƯU ở đây (AI KHÔNG ghi DB — GEN-4). cv_id/jd_id = Guid lỏng tới file_records
// (không cấu hình FK, đồng bộ cách PracticeSession.CvId tham chiếu file_records).
public class CvAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Ref lỏng sang AuthService (chủ sở hữu) — không FK xuyên service.
    public Guid CandidateId { get; set; }

    // Ref lỏng tới file_records (cùng DB interview) — giữ Guid, không FK (như PracticeSession).
    public Guid CvId { get; set; }
    public Guid? JdId { get; set; }

    public JobCategory JobCategory { get; set; }

    public string Summary { get; set; } = string.Empty;

    // jsonb string[] — lưu qua value converter (xem CvAnalysisConfiguration).
    public List<string> Strengths { get; set; } = [];
    public List<string> Weaknesses { get; set; } = [];
    public List<string> Suggestions { get; set; } = [];

    // jsonb? — chỉ set khi request có jd_id (khớp CV↔JD).
    public CvJdMatch? JdMatch { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Value object lưu trong cột jd_match (jsonb) — không phải entity riêng.
public record CvJdMatch(int Score, List<string> MatchedSkills, List<string> MissingSkills);
