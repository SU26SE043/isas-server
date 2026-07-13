namespace Isas.InterviewService.DTOs;

using System.ComponentModel.DataAnnotations;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

// BC7 — request phân tích CV. Doc `{cvId, jdId?}` thiếu nguồn job_category (cột NOT NULL +
// CvAnalysisResponse.jobCategory bắt buộc, AIService không trả jobCategory) → client cấp
// jobCategory như CreatePracticeSessionRequest (tín hiệu tối thiểu). Xem handoff.
// BK6 — jobCategory BẮT BUỘC: kiểu nullable để phân biệt "thiếu" với default enum BA (value 0).
// Thiếu → 400 (Required cho model-binding HTTP + guard service TRƯỚC reserve, xem CvAnalysisService).
// ⚠ Attribute phải nằm trên PARAMETER (KHÔNG [property:]) — ASP.NET (.NET 10) THROW
// InvalidOperationException khi validation attribute property-targeted trên positional record
// (metadata bị ignore) → 500 MỌI request. Bug bắt ở layer-3 API sweep 2026-07-13 (unit test gọi
// service trực tiếp, không qua model-binding nên không thấy).
public record CvAnalysisRequest(
    Guid CvId,
    Guid? JdId,
    [Required] JobCategory? JobCategory
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
