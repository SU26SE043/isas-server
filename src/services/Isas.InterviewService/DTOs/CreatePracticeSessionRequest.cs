namespace Isas.InterviewService.DTOs;

public record CreatePracticeSessionRequest(
    string JobCategory,        // BA | BE | FE
    Guid? CvFileId,            // optional
    string? JdText             // optional
);

public record SubmitAnswerRequest(
    Guid QuestionId,
    string AnswerType,         // text | audio
    string? TextContent,       // bắt buộc nếu text
    Guid? AudioFileId          // bắt buộc nếu audio
);

public record PracticeSessionResponse(
    Guid Id,
    string JobCategory,
    string Status,
    Guid? CvFileId,
    string? JdText,
    decimal? TotalScore,
    string? Feedback,
    DateTime CreatedAt,
    DateTime? SubmittedAt,
    DateTime? ScoredAt,
    IReadOnlyList<QuestionResponse> Questions
);

public record QuestionResponse(
    Guid Id,
    int OrderIndex,
    string Content,
    AnswerResponse? Answer
);

public record AnswerResponse(
    Guid Id,
    string AnswerType,
    string? TextContent,
    Guid? AudioFileId,
    decimal? Score,
    string? Feedback
);

public record PracticeSessionSummary(
    Guid Id,
    string JobCategory,
    string Status,
    decimal? TotalScore,
    DateTime CreatedAt,
    DateTime? ScoredAt
);

public record FileUploadResponse(
    Guid Id,
    string FileType,
    string OriginalName,
    string MimeType,
    long FileSize,
    string ParseStatus
);