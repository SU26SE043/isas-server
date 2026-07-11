using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

public class AiServiceQuestionGenerator : IAiServiceQuestionGenerator
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AiServiceQuestionGenerator> _logger;

    public AiServiceQuestionGenerator(HttpClient httpClient, ILogger<AiServiceQuestionGenerator> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // 1. SỬA TẠI ĐÂY: Định nghĩa Record nhận về mảng String thuần túy theo đúng format Python
    private record FastAPIQuestionsResponse(List<string> Questions);

    public Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText, CancellationToken ct = default)
        => GenerateQuestionsAsync(jobCategory, cvText, jdText, focusCriteria: null, ct);

    // BC14 — overload thêm focusCriteria (roadmap lesson). null/rỗng → hành vi cũ (không gửi field).
    public async Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, CancellationToken ct = default)
    {
        var payload = new
        {
            jobCategory = jobCategory,
            cvText = cvText,
            jdText = jdText,
            // Chỉ gửi khi có (lesson /start). AIService bỏ qua field lạ nếu chưa hỗ trợ (forward-compatible).
            focusCriteria = focusCriteria is { Count: > 0 } ? focusCriteria : null
        };

        var response = await _httpClient.PostAsJsonAsync("/api/v1/generate-questions", payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("FastAPI Error: {StatusCode} - {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"Lỗi gọi FastAPI AIService: {response.StatusCode}");
        }

        // Hứng cục JSON dạng {"questions": ["chuỗi 1", "chuỗi 2"]}
        var result = await response.Content.ReadFromJsonAsync<FastAPIQuestionsResponse>(
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, 
            cancellationToken: ct);

        if (result?.Questions == null)
            return new List<GeneratedQuestion>();

        // 2. SỬA TẠI ĐÂY: Duyệt mảng string từ Python và New từng Object GeneratedQuestion cho C#
        return result.Questions
            .Select((qText, index) => new GeneratedQuestion 
            { 
                // Khởi tạo các thuộc tính theo đúng cấu trúc Class GeneratedQuestion của ông
                Content = qText
                // Nếu class của ông có trường Order hoặc Id thì map luôn ở đây, ví dụ: Order = index + 1
            })
            .ToList();
    }
}