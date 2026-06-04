namespace Isas.InterviewService.Services;

public class AiServiceQuestionGenerator(HttpClient http) : IQuestionGenerator
{
    public async Task<IReadOnlyList<string>> GenerateAsync(
        string jobCategory, string? cvText, string? jdText, CancellationToken ct = default)
    {
        // payload khớp GenerateQuestionsRequest bên FastAPI (camelCase)
        var payload = new
        {
            jobCategory,
            cvText,
            jdText
        };

        var response = await http.PostAsJsonAsync("/generate-questions", payload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GenerateQuestionsResult>(ct)
                     ?? throw new InvalidOperationException("AIService trả về rỗng.");

        return result.Questions;
    }

    // khớp GenerateQuestionsResponse bên FastAPI: {"questions": [...]}
    private sealed record GenerateQuestionsResult(IReadOnlyList<string> Questions);
}