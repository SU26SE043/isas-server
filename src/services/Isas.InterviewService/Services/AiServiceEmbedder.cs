using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// RAG grounding — typed HttpClient gọi AIService `/api/v1/embed` (Contract 1). Gắn X-Internal-Token
// (mẫu AiServiceRepoAnalyzer). Lỗi transport/status/JSON → AiServiceException (caller degrade ungrounded
// hoặc admin thấy 502). base = AiService:BaseUrl.
public class AiServiceEmbedder(HttpClient client, IConfiguration config, ILogger<AiServiceEmbedder> logger)
    : IAiServiceEmbedder
{
    private readonly string? _token = config["Internal:Token"];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private record EmbedResponse(List<List<float>>? Vectors, int Dim, string? Model);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, string taskType, CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/embed")
        {
            Content = JsonContent.Create(new { texts, taskType })
        };
        request.Headers.TryAddWithoutValidation("X-Internal-Token", _token);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Không gọi được AIService /embed");
            throw new AiServiceException("Không gọi được AIService /embed", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("AIService /embed trả {Status}", response.StatusCode);
            throw new AiServiceException($"AIService /embed trả {(int)response.StatusCode}");
        }

        EmbedResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<EmbedResponse>(Json, ct);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "AIService /embed trả JSON không hợp lệ");
            throw new AiServiceException("AIService /embed trả JSON không hợp lệ", ex);
        }

        if (body?.Vectors is null || body.Vectors.Count != texts.Count)
            throw new AiServiceException(
                $"AIService /embed trả {body?.Vectors?.Count ?? 0} vector, cần {texts.Count}");

        return body.Vectors.Select(v => v.ToArray()).ToList();
    }
}
