namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

public record CreatePracticeSessionRequest(
    Guid? CvId,        // optional
    Guid? JdId,        // optional
    JobCategory JobCategory   // BẮT BUỘC — tín hiệu tối thiểu để sinh câu hỏi
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