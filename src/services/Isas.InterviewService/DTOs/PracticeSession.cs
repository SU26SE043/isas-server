namespace Isas.InterviewService.DTOs;

using System.ComponentModel.DataAnnotations;
using Isas.InterviewService.Enums;

// jobCategory BẮT BUỘC — tín hiệu tối thiểu để sinh câu hỏi. Kiểu nullable để phân biệt "thiếu"
// với default enum BA (value 0): thiếu → 400 (Required cho model-binding HTTP + guard service TRƯỚC
// reserve, xem PracticeService.CreateSessionInternalAsync) → KHÔNG giữ credit oan (PAY-5). Trước đây
// non-nullable không [Required] → omitted im lặng thành BA(0) VÀ vẫn reserve 1 credit (B2C audit P1).
// ⚠ Attribute phải nằm trên PARAMETER (KHÔNG [property:]) — ASP.NET (.NET 10) THROW khi validation
// attribute property-targeted trên positional record → 500 mọi request (mẫu CvAnalysisRequest/BK6).
// JdText: JD nhập THẲNG dạng text (khỏi phải upload PDF trước) — mượn nguyên quy ước C11 của
// B2B/Campaign (`jdText` + "text ưu tiên file") để 2 dòng sản phẩm nhất quán. Gửi cả `jdText` lẫn
// `jdId` → TEXT THẮNG, bỏ file (xem PracticeService.CreateSessionInternalAsync). Đặt CUỐI + có
// default → mọi call site positional cũ (RoadmapLessonService, test) không phải sửa.
// TimeLimitSec (F2): thời lượng mỗi câu ứng viên chọn — 60/120/240; null = 120 (hành vi cũ).
// ⚠ Đặt CUỐI + có default: call site positional (RoadmapLessonService, test cũ) không phải sửa.
public record CreatePracticeSessionRequest(
    Guid? CvId,        // optional
    Guid? JdId,        // optional
    [Required] JobCategory? JobCategory,
    string? JdText = null,   // optional — ưu tiên hơn JdId
    int? TimeLimitSec = null // optional — 60/120/240; null = mặc định 120
);

// I1 (B2B): Campaign gửi tiêu chí CÓ CẤU TRÚC kèm khi tạo session → materialize thành rubric_criteria(campaign_id).
public record CampaignCriterionInput(
    string Name,
    string? Description,
    decimal Weight,    // Σ/campaign = 1 (chuẩn hoá phía Campaign)
    int MaxScore
);

// I1 (B2B): tạo session bài thi của 1 campaign. Câu hỏi + tiêu chí do Campaign cấp (không gọi AI sinh).
// I2: ExpiresAt = hạn chót nhận bài (campaigns.expires_at) → set session.Deadline; null = không hard-deadline.
// Phỏng vấn THÍCH ỨNG (B2B): Adaptive*/MaxFollowUps/MaxQuestions do Campaign/HR bật (optional; null = tắt).
// Seed = toàn bộ campaign questions (ai cũng nhận) → câu thích ứng thêm ở đuôi, chấm theo CÙNG tiêu chí.
public record CreateCampaignSessionRequest(
    Guid CampaignId,
    Guid OrgId,        // BK14: chủ ví credit (owner=Org) để reserve khi tạo session B2B (PAY-6)
    JobCategory JobCategory,
    IReadOnlyList<string> Questions,
    IReadOnlyList<CampaignCriterionInput> Criteria,
    DateTime? ExpiresAt = null,
    bool? AdaptiveEnabled = null,
    int? MaxFollowUps = null,
    int? MaxQuestions = null
);

// D2: request cho endpoint internal create-or-get session B2B (CampaignService gọi khi ứng viên bấm
// "Start Interview"). candidateId đi kèm (Campaign đã provision qua Auth); jobCategory là STRING để
// TryParse mềm (ref lỏng xuyên service — Campaign gửi Domain, không lệ thuộc enum Interview).
public record CreateCampaignSessionInternalRequest(
    Guid CandidateId,
    Guid CampaignId,
    Guid OrgId,        // BK14: chủ ví credit org (Campaign gửi campaign.OrgId) → reserve owner=Org (PAY-6)
    string JobCategory,
    IReadOnlyList<string> Questions,
    IReadOnlyList<CampaignCriterionInput> Criteria,
    // I2: hạn chót nhận bài (campaigns.expires_at). Campaign gửi kèm → set session.Deadline; null =
    // không hard-deadline (chỉ giới hạn từng câu). Campaign gửi field này là FOLLOW-UP nhỏ ngoài scope I2.
    DateTime? ExpiresAt = null,
    // Phỏng vấn THÍCH ỨNG (B2B): Campaign/HR bật toggle + trần (optional; null = tắt → luồng batch tĩnh cũ).
    bool? AdaptiveEnabled = null,
    int? MaxFollowUps = null,
    int? MaxQuestions = null
);
public record PracticeSessionResponse(
    Guid Id,
    string Status,
    string JobCategory,
    Guid? CvId, Guid? JdId,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<QuestionResponse> Questions,
    SessionResultResponse? Result = null   // BC9 — chỉ khi status=Scored & campaign_id=null (B2C); null nếu chưa
);

public record QuestionResponse(
    Guid Id,
    int OrderNo,
    string Content,
    int TimeLimitSec,
    AnswerResponse? Answer,
    string Kind = "Seed"   // phỏng vấn THÍCH ỨNG — Seed | FollowUp | Clarify | NewQuestion (default an toàn cho client cũ)
);

public record AnswerResponse(
    Guid Id,
    string Status,
    int DurationSec,
    string? Transcript,
    IReadOnlyList<AnswerScoreResponse> Scores,
    bool NeedsReview = false   // E10 — self-consistency: spread điểm giữa các attempt vượt ngưỡng → cần soi lại (nullable-default → không phá client)
);

public record AnswerScoreResponse(
    Guid CriterionId,
    decimal Score,
    string? Reasoning,
    int RubricVersion,
    int? LevelMatched = null,  // E9 — mức khớp khi neo theo rubric_levels; null nếu chưa neo (nullable → không phá client)
    // Tên + thang điểm tiêu chí, để client HIỂN THỊ được mà không phải tra ngược id.
    // Bắt ở e2e 2026-07-18: client chỉ nhận `criterionId` nên breakdown điểm hiện trơ "Điểm tiêu chí"
    // (B2C) và mã GUID (transcript B2B). Tra ngược KHÔNG khả thi: `rubric_criteria` của campaign được
    // mint `Guid.NewGuid()` lúc materialize (PracticeService) nên id này KHÁC id `campaign_criteria`.
    // Nullable + đặt cuối: client cũ không vỡ; caller quên `.ThenInclude(Criterion)` thì ra null chứ
    // không ném NRE.
    string? CriterionName = null,
    int? MaxScore = null
);

public record PracticeSessionSummary(
    Guid Id,
    string Status,
    string JobCategory,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    decimal? OverallScore   // BC9 — điểm tổng 0–100 nếu đã Scored (B2C); null nếu chưa
);

// BC9 — tổng kết cả buổi luyện B2C (số liệu), đọc từ practice_sessions + session_criterion_scores.
public record SessionResultResponse(
    decimal OverallScore,          // 0–100, trung bình cộng pct các tiêu chí (equal weight)
    int AnsweredCount,             // số câu đã chấm (có điểm)
    int TotalQuestions,            // tổng số câu của buổi
    IReadOnlyList<CriterionScoreResponse> CriteriaScores,
    IReadOnlyList<Guid> NeedsImprovement,   // criterionId của tiêu chí dưới ngưỡng
    string? OverallComment = null,  // BC10 — nhận xét chung (AI); null trong BC9
    CvVsAnswerReportResponse? CvVsAnswer = null   // BC8 — đối chiếu CV↔trả lời; null nếu không có CV đã phân tích
);

// BC8 — báo cáo "CV vs câu trả lời": đọc dữ liệu SẴN CÓ (không AI, không call ngoài).
// CvStrengths = strengths (+matched skills) từ cv_analyses (BC7); Gaps = tiêu chí VỪA yếu
// (needs_improvement, BC9) VỪA được CV thể hiện mạnh (token khớp tên tiêu chí ↔ strength CV).
public record CvVsAnswerReportResponse(
    IReadOnlyList<string> CvStrengths,
    IReadOnlyList<CvAnswerGapResponse> Gaps
);

// BC8 — một điểm "CV mạnh nhưng trả lời yếu": tiêu chí answer dưới ngưỡng + bằng chứng CV khớp.
public record CvAnswerGapResponse(
    Guid CriterionId,
    string CriterionName,
    decimal Percentage,          // % điểm answer đạt (dưới ngưỡng cải thiện)
    int MaxScore,
    IReadOnlyList<string> CvEvidence   // strength/skill CV khớp tiêu chí này (giải thích vì sao coi là "CV mạnh")
);

// BC9 — điểm mỗi tiêu chí trong buổi luyện.
public record CriterionScoreResponse(
    Guid CriterionId,
    string Name,
    decimal AverageScore,   // điểm đạt được (TB qua các câu đã chấm)
    int MaxScore,           // điểm tối đa tiêu chí → hiển thị "averageScore/maxScore"
    decimal Percentage,     // averageScore / maxScore × 100 (0–100)
    decimal Weight          // trọng số rubric (B2C chỉ hiển thị, không dùng cho overall)
);