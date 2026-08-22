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

    /// <summary>
    /// Trình độ NGHỀ NGHIỆP hiện tại mà CV chứng minh được (Fresher/Junior/Middle/Senior).
    /// Khác hẳn <c>roadmaps.level</c> — cái đó là trình độ MỤC TIÊU người dùng tự chọn ở wizard.
    /// Prompt sinh roadmap dùng trường này làm SÀN: bỏ phần nhập môn người học đã nắm.
    ///
    /// <para><c>null</c> = CV KHÔNG đủ căn cứ, và đó là trạng thái hợp lệ chứ không phải lỗi —
    /// đo trên production: 87% bản phân tích CV không nhắc tới trình độ ở đâu. Vì vậy cột
    /// nullable và CHECK cho phép NULL; KHÔNG có default, vì mặc định một mức nào đó là bịa cho
    /// phần lớn người dùng.</para>
    /// </summary>
    public string? CurrentLevel { get; set; }

    // jsonb string[] — lưu qua value converter (xem CvAnalysisConfiguration).
    public List<string> Strengths { get; set; } = [];
    public List<string> Weaknesses { get; set; } = [];
    public List<string> Suggestions { get; set; } = [];

    // jsonb? — chỉ set khi request có jd_id (khớp CV↔JD).
    public CvJdMatch? JdMatch { get; set; }

    // null = LEGACY; khác null = REQUIREMENT. Gộp hai priority trong một jsonb để lịch sử
    // dựng lại đúng hai danh sách theo thứ tự người dùng gửi.
    public List<CvRequirementMatch>? RequirementMatches { get; set; }
    public List<CvSectionAnchor>? CvSections { get; set; }
    public List<CvAnalysisCitation>? Citations { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Value object lưu trong cột jd_match (jsonb) — không phải entity riêng.
public record CvJdMatch(int Score, List<string> MatchedSkills, List<string> MissingSkills);

public record CvRequirementMatch(
    string RequirementId,
    string Priority,
    string Text,
    string Level,
    string Evidence,
    int? Page = null,
    string? SectionTitle = null
);

public record CvSectionAnchor(string Title, string Kind, string StartsWith);

public record CvAnalysisCitation(
    string ChunkId,
    string Content,
    string? SourceUrl,
    string? SourceTitle
);
