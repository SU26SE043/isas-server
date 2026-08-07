using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// BC10 — typed HttpClient gọi AIService `/summarize-session` (sync). Mẫu AiServiceCvAnalyzer/RoadmapGenerator.
// Request {jobCategory, overallScore, criteriaScores:[{name,percentage,needsImprovement}]} → response {overallComment}.
// AI KHÔNG ghi DB (GEN-4) — chỉ trả nhận xét; Interview tự lưu. Lỗi → AiServiceException (caller best-effort nuốt).
public class AiServiceSessionSummarizer : IAiServiceSessionSummarizer
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AiServiceSessionSummarizer> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AiServiceSessionSummarizer(HttpClient httpClient, ILogger<AiServiceSessionSummarizer> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // Shape res AIService — chỉ nhận xét text.
    private record SummarizeApiResponse(string? OverallComment);

    public async Task<string> SummarizeAsync(
        string jobCategory, decimal overallScore,
        IReadOnlyList<SessionSummaryCriterion> criteriaScores,
        CancellationToken ct = default)
        => await SummarizeAsync(jobCategory, overallScore, criteriaScores, "vi", ct);

    public async Task<string> SummarizeAsync(
        string jobCategory, decimal overallScore,
        IReadOnlyList<SessionSummaryCriterion> criteriaScores,
        string language,
        CancellationToken ct = default)
    {
        var payload = new
        {
            jobCategory,
            language,
            overallScore,
            criteriaScores = criteriaScores.Select(c => new
            {
                name = c.Name,
                percentage = c.Percentage,
                needsImprovement = c.NeedsImprovement
            })
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync("/api/v1/summarize-session", payload, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được AIService /summarize-session");
            throw new AiServiceException("Không gọi được AIService /summarize-session", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /summarize-session lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /summarize-session trả {(int)response.StatusCode}");
        }

        SummarizeApiResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<SummarizeApiResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "AIService /summarize-session trả JSON không hợp lệ");
            throw new AiServiceException("AIService /summarize-session trả JSON không hợp lệ", ex);
        }

        if (body is null || string.IsNullOrWhiteSpace(body.OverallComment))
            throw new AiServiceException("AIService /summarize-session trả rỗng");

        return body.OverallComment;
    }
}
