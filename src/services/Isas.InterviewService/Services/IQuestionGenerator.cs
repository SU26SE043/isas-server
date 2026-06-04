namespace Isas.InterviewService.Services;

public interface IQuestionGenerator
{
    Task<IReadOnlyList<string>> GenerateAsync(
        string jobCategory, string? cvText, string? jdText, CancellationToken ct = default);
}
