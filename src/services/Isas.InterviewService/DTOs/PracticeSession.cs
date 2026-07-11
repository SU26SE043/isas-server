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
public record CreateCampaignSessionRequest(
    Guid CampaignId,
    JobCategory JobCategory,
    IReadOnlyList<string> Questions,
    IReadOnlyList<CampaignCriterionInput> Criteria
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
    int RubricVersion
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
    string? OverallComment = null  // BC10 — nhận xét chung (AI); null trong BC9
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