using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

public interface IAiServiceQuestionGenerator
{
    Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText, CancellationToken ct = default);
}

// Nhét cái này vào chung file DTOs của ông
public class GeneratedQuestion
{
    public string Content { get; set; } = string.Empty;
}