using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

public class AiServiceQuestionGenerator : IAiServiceQuestionGenerator
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<AiServiceQuestionGenerator> _logger;
    private readonly string? _token;

    public AiServiceQuestionGenerator(
        HttpClient httpClient, IConfiguration config, ILogger<AiServiceQuestionGenerator> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _token = config["Internal:Token"];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Contract 2 — questions GIỮ NGUYÊN (Campaign B2B còn gọi); citations ADDITIVE (chỉ có khi truyền grounding).
    private record FastAPIQuestionsResponse(List<string>? Questions, List<CitationApi>? Citations);
    private record CitationApi(int QuestionIndex, List<string>? CitedChunkIds);

    public Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText, CancellationToken ct = default)
        => GenerateQuestionsAsync(jobCategory, cvText, jdText, focusCriteria: null, count: null, ct);

    // BC14 (focusCriteria) + F2b (count). null = không ghi đè → AIService dùng mặc định của nó.
    public async Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count, CancellationToken ct = default)
    {
        var result = await GenerateQuestionsAsync(
            jobCategory, cvText, jdText, focusCriteria, count, grounding: null, ct);
        return result.Questions;
    }

    // RAG grounding — overload GROUNDED (đường DUY NHẤT gọi AIService; 2 overload trên delegate về đây).
    public async Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, CancellationToken ct = default)
        => await GenerateQuestionsAsync(jobCategory, cvText, jdText, focusCriteria, count, grounding, "vi", ct);

    public async Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, string language, CancellationToken ct = default)
    {
        var payload = new
        {
            jobCategory,
            cvText,
            jdText,
            // ⚠ Field TỪNG bị AIService nuốt im lặng (pydantic extra='ignore') — đã khai ở schema (F2b/W1).
            focusCriteria = focusCriteria is { Count: > 0 } ? focusCriteria : null,
            count,
            language,
            // RAG grounding — chunk truy hồi (Contract 2). Chỉ gửi khi có → AIService chèn block "TÀI LIỆU
            // THAM CHIẾU UY TÍN" + trả citations. null → sinh ungrounded như cũ (Campaign B2B không truyền).
            grounding = grounding is { Count: > 0 }
                ? grounding.Select(g => new { chunkId = g.ChunkId, content = g.Content, sourceUrl = g.SourceUrl, sourceTitle = g.SourceTitle })
                : null
        };

        // RAG grounding — /generate-questions là endpoint AIService (GEN-1/GEN-7 internal-only) → gắn
        // X-Internal-Token. TRƯỚC ĐÂY THIẾU (chỉ chạy được vì AIService chưa gate endpoint sinh); thêm để
        // khớp fail-closed khi W1 gate /generate-questions.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/generate-questions")
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
            // Upstream không gọi được (transport/timeout) = AIService lỗi → AiServiceException để controller
            // map 502 (không nuốt thành 400).
            _logger.LogError(ex, "Không gọi được AIService /generate-questions");
            throw new AiServiceException("Không gọi được AIService /generate-questions", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("FastAPI Error: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /generate-questions trả {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadFromJsonAsync<FastAPIQuestionsResponse>(Json, ct);

        var questions = (body?.Questions ?? new List<string>())
            .Select(qText => new GeneratedQuestion { Content = qText })
            .ToList();

        var citations = (body?.Citations ?? new List<CitationApi>())
            .Select(c => new QuestionCitationDto(c.QuestionIndex, c.CitedChunkIds ?? new List<string>()))
            .ToList();

        return new GeneratedQuestionsResult(questions, citations);
    }
}
