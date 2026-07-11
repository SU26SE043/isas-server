using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// BC12 — typed HttpClient gọi AIService `/generate-roadmap` (sync). Mẫu AiServiceCvAnalyzer.
// Request {jobCategory, level, weaknesses?, cvText?} → response {milestones:[{title,focusCriteria[],lessons:[{title}]}]}.
public class AiServiceRoadmapGenerator : IAiServiceRoadmapGenerator
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AiServiceRoadmapGenerator> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AiServiceRoadmapGenerator(HttpClient httpClient, ILogger<AiServiceRoadmapGenerator> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // Shape res AIService — chỉ cấu trúc (không điểm).
    private record RoadmapApiResponse(List<MilestoneApi>? Milestones);
    private record MilestoneApi(string? Title, List<string>? FocusCriteria, List<LessonApi>? Lessons);
    private record LessonApi(string? Title);

    public async Task<RoadmapGenAiResult> GenerateAsync(
        string jobCategory, string level,
        IReadOnlyList<RoadmapWeakness>? weaknesses, string? cvText,
        CancellationToken ct = default)
    {
        var payload = new
        {
            jobCategory,
            level,
            // rỗng/null → AI sinh roadmap chuẩn theo level (schema WeaknessScore: criterionName + percentage).
            weaknesses = weaknesses?.Select(w => new { criterionName = w.CriterionName, percentage = w.Percentage }),
            cvText
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync("/api/v1/generate-roadmap", payload, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được AIService /generate-roadmap");
            throw new AiServiceException("Không gọi được AIService /generate-roadmap", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /generate-roadmap lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /generate-roadmap trả {(int)response.StatusCode}");
        }

        RoadmapApiResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<RoadmapApiResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "AIService /generate-roadmap trả JSON không hợp lệ");
            throw new AiServiceException("AIService /generate-roadmap trả JSON không hợp lệ", ex);
        }

        if (body?.Milestones is null || body.Milestones.Count == 0)
            throw new AiServiceException("AIService /generate-roadmap trả rỗng");

        var milestones = body.Milestones.Select(m => new GeneratedMilestone(
            m.Title ?? string.Empty,
            m.FocusCriteria ?? [],
            (m.Lessons ?? []).Select(l => new GeneratedLesson(l.Title ?? string.Empty)).ToList()
        )).ToList();

        return new RoadmapGenAiResult(milestones);
    }

    // Shape res AIService /generate-lesson-theory (GenerateLessonTheoryResponse) — chỉ markdown.
    private record LessonTheoryApiResponse(string? TheoryMarkdown);

    // BC14 — POST /generate-lesson-theory {jobCategory, level, lessonTitle, focusCriteria[], weaknesses?}
    // → {theoryMarkdown}. Sync như /generate-roadmap. Lỗi → AiServiceException (→ 502).
    public async Task<string> GenerateLessonTheoryAsync(
        string jobCategory, string level, string lessonTitle,
        IReadOnlyList<string> focusCriteria, IReadOnlyList<string>? weaknesses,
        CancellationToken ct = default)
    {
        var payload = new
        {
            jobCategory,
            level,
            lessonTitle,
            focusCriteria = focusCriteria ?? [],
            weaknesses
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync("/api/v1/generate-lesson-theory", payload, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được AIService /generate-lesson-theory");
            throw new AiServiceException("Không gọi được AIService /generate-lesson-theory", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /generate-lesson-theory lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /generate-lesson-theory trả {(int)response.StatusCode}");
        }

        LessonTheoryApiResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<LessonTheoryApiResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "AIService /generate-lesson-theory trả JSON không hợp lệ");
            throw new AiServiceException("AIService /generate-lesson-theory trả JSON không hợp lệ", ex);
        }

        if (body is null || string.IsNullOrWhiteSpace(body.TheoryMarkdown))
            throw new AiServiceException("AIService /generate-lesson-theory trả rỗng");

        return body.TheoryMarkdown;
    }
}
