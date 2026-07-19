using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// Phỏng vấn THÍCH ỨNG — gọi AIService `/decide-next` (máy-máy, X-Internal-Token, KHÔNG qua gateway).
// Nhái mẫu AiServiceCvAnalyzer/CreditReservationClient. Lỗi transport/non-2xx → AiServiceException →
// caller (AnswerService) nuốt + degrade về luồng tĩnh (answer đã lưu, worker transcribe async như cũ).
public class AiServiceInterviewDecider : IAiServiceInterviewDecider
{
    private readonly HttpClient _httpClient;
    private readonly string? _internalToken;
    private readonly ILogger<AiServiceInterviewDecider> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AiServiceInterviewDecider(
        HttpClient httpClient, IConfiguration config, ILogger<AiServiceInterviewDecider> logger)
    {
        _httpClient = httpClient;
        _internalToken = config["Internal:Token"];   // /decide-next gate bằng X-Internal-Token (GEN-7)
        _logger = logger;
    }

    private record DecideNextApiResponse(
        string? Action, string? NextQuestion, string? Transcript, string? Reason,
        // F11 — AIService đo chỉ số cách nói ngay trong lượt transcribe đồng bộ này.
        DeliveryMetricsDto? DeliveryMetrics);

    public async Task<DecideNextResult> DecideNextAsync(
        string audioObjectKey,
        string jobCategory,
        string currentQuestion,
        IReadOnlyList<DecideTurnDto> history,
        int askedCount,
        int followUpCount,
        int maxQuestions,
        int maxFollowUps,
        IReadOnlyList<DecideCriterionDto> criteria,
        CancellationToken ct = default)
    {
        var payload = new
        {
            jobCategory,
            audioObjectKey,
            currentQuestion,
            history = history.Select(h => new { question = h.Question, answer = h.Answer, kind = h.Kind }),
            askedCount,
            followUpCount,
            maxQuestions,
            maxFollowUps,
            criteria = criteria.Select(c => new { name = c.Name, description = c.Description })
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/decide-next")
        {
            Content = JsonContent.Create(payload)
        };
        msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(msg, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Không gọi được AIService /decide-next");
            throw new AiServiceException("Không gọi được AIService /decide-next", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("AIService /decide-next lỗi: {StatusCode} - {Error}", response.StatusCode, error);
            throw new AiServiceException($"AIService /decide-next trả {(int)response.StatusCode}");
        }

        DecideNextApiResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<DecideNextApiResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "AIService /decide-next trả JSON không hợp lệ");
            throw new AiServiceException("AIService /decide-next trả JSON không hợp lệ", ex);
        }

        if (body?.Action is null)
            throw new AiServiceException("AIService /decide-next trả thiếu action");

        return new DecideNextResult(
            Action: body.Action,
            NextQuestion: body.NextQuestion,
            Transcript: body.Transcript,
            Reason: body.Reason,
            DeliveryMetrics: body.DeliveryMetrics);   // F11 (null nếu AIService bản cũ / không đo được)
    }
}
