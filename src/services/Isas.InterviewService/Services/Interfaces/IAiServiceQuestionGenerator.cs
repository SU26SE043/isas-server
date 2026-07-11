using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

public interface IAiServiceQuestionGenerator
{
    Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText, CancellationToken ct = default);

    // BC14 — sinh câu hỏi bám tiêu chí trọng tâm (roadmap lesson /start). focusCriteria đưa vào payload
    // AIService (best-effort: schema AIService bổ sung field thì mới thực sự cá nhân hoá được).
    Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, CancellationToken ct = default);
}

// Nhét cái này vào chung file DTOs của ông
public class GeneratedQuestion
{
    public string Content { get; set; } = string.Empty;
}