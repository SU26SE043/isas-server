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
// JdText: JD nhập THẲNG dạng text (khỏi phải upload PDF trước) — quy ước C11 của B2B/Campaign,
// "text ưu tiên file". Có JD (text HOẶC file) → AI trả `jdMatch`. Đặt CUỐI + có default → call site
// positional cũ không phải sửa.
public record CvAnalysisRequest(
    Guid CvId,
    Guid? JdId,
    [Required] JobCategory? JobCategory,
    string? JdText = null,   // optional — ưu tiên hơn JdId
    IReadOnlyList<CvRequirementInput>? MustHave = null,
    IReadOnlyList<CvRequirementInput>? NiceToHave = null
);

// N7 — FE chỉ gửi `text`; InterviewService tự mint RequirementId trước khi gọi AIService.
public record CvRequirementInput(string? RequirementId, string Text);

// Kết quả AI đọc từ AIService `/analyze-cv` (B2C — bỏ criterionMatches/overallMatchScore của B2B).
public record CvAnalysisAiResult(
    string Summary,
    List<string> Strengths,
    List<string> Weaknesses,
    List<string> Suggestions,
    CvJdMatch? JdMatch,   // chỉ khi gửi kèm jdText ở LEGACY
    IReadOnlyList<CvRequirementMatch>? RequirementMatches = null,
    IReadOnlyList<CvSectionAnchor>? CvSections = null,
    IReadOnlyList<CvAnalysisCitation>? Citations = null
);

public record JdMatchResponse(
    int Score,
    IReadOnlyList<string> MatchedSkills,
    IReadOnlyList<string> MissingSkills
);

public record RequirementSummaryBucket(int Total, int Strong, int Partial, int Weak);

public record RequirementSummary(
    RequirementSummaryBucket MustHave,
    RequirementSummaryBucket NiceToHave
);

public record CvRequirementListItem(
    string RequirementId,
    string Priority,
    string Text,
    string Level
);

public record CvAnalysisListResponse(
    Guid Id,
    Guid CvId,
    Guid? JdId,
    string JobCategory,
    string Summary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Weaknesses,
    IReadOnlyList<string> Suggestions,
    JdMatchResponse? JdMatch,
    DateTime CreatedAt,
    IReadOnlyList<CvRequirementListItem>? MustHaveMatches = null,
    IReadOnlyList<CvRequirementListItem>? NiceToHaveMatches = null,
    RequirementSummary? RequirementSummary = null
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
    DateTime CreatedAt,
    IReadOnlyList<CvRequirementMatch>? MustHaveMatches = null,
    IReadOnlyList<CvRequirementMatch>? NiceToHaveMatches = null,
    RequirementSummary? RequirementSummary = null,
    IReadOnlyList<CvSectionAnchor>? CvSections = null,
    IReadOnlyList<CvAnalysisCitation>? Citations = null
);
