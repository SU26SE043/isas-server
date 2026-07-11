namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

// BC7 — request phân tích CV. Doc `{cvId, jdId?}` thiếu nguồn job_category (cột NOT NULL +
// CvAnalysisResponse.jobCategory bắt buộc, AIService không trả jobCategory) → client cấp
// jobCategory như CreatePracticeSessionRequest (tín hiệu tối thiểu). Xem handoff.
public record CvAnalysisRequest(
    Guid CvId,
    Guid? JdId,
    JobCategory JobCategory
);

// Kết quả AI đọc từ AIService `/analyze-cv` (B2C — bỏ criterionMatches/overallMatchScore của B2B).
public record CvAnalysisAiResult(
    string Summary,
    List<string> Strengths,
    List<string> Weaknesses,
    List<string> Suggestions,
    CvJdMatch? JdMatch   // chỉ khi gửi kèm jdText
);

public record JdMatchResponse(
    int Score,
    IReadOnlyList<string> MatchedSkills,
    IReadOnlyList<string> MissingSkills
);

public record CvAnalysisResponse(
    Guid Id,
    Guid CvId,
    Guid? JdId,
    string JobCategory,
    string Summary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Weaknesses,
    IReadOnlyList<string> Suggestions,
    JdMatchResponse? JdMatch,   // chỉ khi có jdId
    DateTime CreatedAt
);
