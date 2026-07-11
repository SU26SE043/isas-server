namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

public record CreatePracticeSessionRequest(
    Guid? CvId,        // optional
    Guid? JdId,        // optional
    JobCategory JobCategory   // BẮT BUỘC — tín hiệu tối thiểu để sinh câu hỏi
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
public record CreateCampaignSessionRequest(
    Guid CampaignId,
    JobCategory JobCategory,
    IReadOnlyList<string> Questions,
    IReadOnlyList<CampaignCriterionInput> Criteria,
    DateTime? ExpiresAt = null
);

// D2: request cho endpoint internal create-or-get session B2B (CampaignService gọi khi ứng viên bấm
// "Start Interview"). candidateId đi kèm (Campaign đã provision qua Auth); jobCategory là STRING để
// TryParse mềm (ref lỏng xuyên service — Campaign gửi Domain, không lệ thuộc enum Interview).
public record CreateCampaignSessionInternalRequest(
    Guid CandidateId,
    Guid CampaignId,
    string JobCategory,
    IReadOnlyList<string> Questions,
    IReadOnlyList<CampaignCriterionInput> Criteria,
    // I2: hạn chót nhận bài (campaigns.expires_at). Campaign gửi kèm → set session.Deadline; null =
    // không hard-deadline (chỉ giới hạn từng câu). Campaign gửi field này là FOLLOW-UP nhỏ ngoài scope I2.
    DateTime? ExpiresAt = null
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
    AnswerResponse? Answer
);

public record AnswerResponse(
    Guid Id,
    string Status,
    int DurationSec,
    string? Transcript,
    IReadOnlyList<AnswerScoreResponse> Scores
);

public record AnswerScoreResponse(
    Guid CriterionId,
    decimal Score,
    string? Reasoning,
    int RubricVersion,
    int? LevelMatched = null   // E9 — mức khớp khi neo theo rubric_levels; null nếu chưa neo (nullable → không phá client)
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