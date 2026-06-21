namespace Isas.InterviewService.Services.Interfaces;

public interface IQuestionGenerator
{
    Task<IReadOnlyList<string>> GenerateAsync(
        string jobCategory, string? cvText, string? jdText, CancellationToken ct = default);
}
