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

    private record JdRequirementsApiResponse(
        List<JdRequirementApi>? MustHave,
        List<JdRequirementApi>? NiceToHave);

    // JdQuote — câu nguyên văn trong JD của user sinh ra requirement (AIService đã verify là
    // substring thật của jdText, không verify được thì trả null). Khác hẳn Citations = tài liệu
    // chuẩn ngành từ Qdrant. Nullable ⇒ AIService bản cũ (chưa có field) vẫn map được, ra null.
    private record JdRequirementApi(string? Text, List<GroundingApi>? Citations, string? JdQuote);
    private record GroundingApi(string? ChunkId, string? Content, string? SourceUrl, string? SourceTitle);

    private record JdMatchApi(int Score, List<string>? MatchedSkills, List<string>? MissingSkills);

    public async Task<CvAnalysisAiResult> AnalyzeAsync(
        string jobCategory,
        string cvText,
        string? jdText,
        CancellationToken ct = default,
        IReadOnlyList<CvRequirementInput>? mustHave = null,
        IReadOnlyList<CvRequirementInput>? niceToHave = null,
        IReadOnlyList<GroundingChunk>? grounding = null)
    {
        var payload = new
        {
            cvText,
            jobCategory,
            jdText,
            mustHave,
            niceToHave,
            grounding
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

    public async Task<(IReadOnlyList<JdRequirementSuggestion> MustHave,
                       IReadOnlyList<JdRequirementSuggestion> NiceToHave)> SuggestJdRequirementsAsync(
        string jobCategory,
        string jdText,
        IReadOnlyList<GroundingChunk>? grounding,
        CancellationToken ct = default)
    {
        var payload = new
        {
            jdText,
            jobCategory,
            grounding
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/suggest-jd-requirements")
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
            _logger.LogError(ex, "Không gọi được AIService /suggest-jd-requirements");
            throw new AiServiceException("Không gọi được AIService /suggest-jd-requirements", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /suggest-jd-requirements lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /suggest-jd-requirements trả {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadFromJsonAsync<JdRequirementsApiResponse>(Json, ct)
            ?? throw new AiServiceException("AIService /suggest-jd-requirements trả rỗng");

        static List<JdRequirementSuggestion> Map(IEnumerable<JdRequirementApi>? items)
            => (items ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .Select(x => new JdRequirementSuggestion(
                    x.Text!.Trim(),
                    (x.Citations ?? [])
                        .Where(c => !string.IsNullOrWhiteSpace(c.ChunkId) && c.Content is not null)
                        .Select(c => new Citation(
                            c.ChunkId!, c.SourceUrl ?? string.Empty, c.SourceTitle ?? string.Empty))
                        .ToList(),
                    // Chuỗi rỗng/khoảng trắng ⇒ null: "không có quote" phải có ĐÚNG MỘT biểu diễn
                    // trên wire để FE chỉ cần kiểm null.
                    string.IsNullOrWhiteSpace(x.JdQuote) ? null : x.JdQuote.Trim()))
                .ToList();

        return (Map(body.MustHave), Map(body.NiceToHave));
    }
}
