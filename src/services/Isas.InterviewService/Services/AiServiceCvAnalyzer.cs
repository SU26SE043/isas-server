using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// BC7 — typed HttpClient gọi AIService `/analyze-cv` (sync). AI KHÔNG ghi DB — chỉ trả kết quả.
// Không gửi criteria[] (đó là nhánh B2B C14 async) → res bỏ criterionMatches/overallMatchScore.
public class AiServiceCvAnalyzer : IAiServiceCvAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly string? _token;
    private readonly ILogger<AiServiceCvAnalyzer> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AiServiceCvAnalyzer(
        HttpClient httpClient, IConfiguration config, ILogger<AiServiceCvAnalyzer> logger)
    {
        _httpClient = httpClient;
        // GEN-7: /analyze-cv nay gate X-Internal-Token (fail-closed) → đính token như
        // AiServiceQuestionGenerator. Thiếu token → 502 ở controller, KHÔNG mất credit (reserve
        // xảy ra trước, và AI lỗi → release — xem CvAnalysisService/BC7b).
        _token = config["Internal:Token"];
        _logger = logger;
    }

    // Shape res AIService — nullable field để giữ nguyên LEGACY khi AI không trả requirement data.
    private record AnalyzeCvApiResponse(
        string? Summary,
        List<string>? Strengths,
        List<string>? Weaknesses,
        List<string>? Suggestions,
        JdMatchApi? JdMatch,
        List<CvRequirementMatch>? RequirementMatches,
        List<CvSectionAnchor>? CvSections,
        List<CvAnalysisCitation>? Citations);

    private record JdMatchApi(int Score, List<string>? MatchedSkills, List<string>? MissingSkills);

    public async Task<CvAnalysisAiResult> AnalyzeAsync(
        string jobCategory,
        string cvText,
        string? jdText,
        CancellationToken ct = default,
        IReadOnlyList<CvRequirementInput>? mustHave = null,
        IReadOnlyList<CvRequirementInput>? niceToHave = null)
    {
        var payload = new
        {
            cvText,
            jobCategory,
            jdText,
            mustHave,
            niceToHave
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/analyze-cv")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("X-Internal-Token", _token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được AIService /analyze-cv");
            throw new AiServiceException("Không gọi được AIService /analyze-cv", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /analyze-cv lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /analyze-cv trả {(int)response.StatusCode}");
        }

        AnalyzeCvApiResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<AnalyzeCvApiResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "AIService /analyze-cv trả JSON không hợp lệ");
            throw new AiServiceException("AIService /analyze-cv trả JSON không hợp lệ", ex);
        }

        if (body is null)
            throw new AiServiceException("AIService /analyze-cv trả rỗng");

        CvJdMatch? jdMatch = body.JdMatch is null
            ? null
            : new CvJdMatch(
                body.JdMatch.Score,
                body.JdMatch.MatchedSkills ?? [],
                body.JdMatch.MissingSkills ?? []);

        return new CvAnalysisAiResult(
            Summary: body.Summary ?? string.Empty,
            Strengths: body.Strengths ?? [],
            Weaknesses: body.Weaknesses ?? [],
            Suggestions: body.Suggestions ?? [],
            JdMatch: jdMatch,
            RequirementMatches: body.RequirementMatches,
            CvSections: body.CvSections,
            Citations: body.Citations);
    }
}
