namespace Isas.InterviewService.DTOs;

public record UploadAnswerResult(
    Guid AnswerId,
    Guid QuestionId,
    string Status);
