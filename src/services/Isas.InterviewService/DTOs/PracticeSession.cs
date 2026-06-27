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
    IReadOnlyList<QuestionResponse> Questions
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
    DateTime? CompletedAt
);