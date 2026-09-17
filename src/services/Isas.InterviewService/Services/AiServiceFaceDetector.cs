using System.Net.Http.Json;
using System.Text.Json;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// B2C coaching (2026-09-17) — gọi AIService `/face-detect` (máy-máy, X-Internal-Token, KHÔNG qua
// gateway). Nhái mẫu AiServiceInterviewDecider/AiServiceSpeechSynthesizer. Lỗi transport/non-2xx →
// AiServiceException → caller (PracticeFaceCheckService) NÉM tiếp — khác /decide-next (degrade im
// lặng): ảnh + dòng sổ đã ghi trước khi gọi (không mồ côi), nên ném ra ngoài là an toàn.
public class AiServiceFaceDetector : IAiServiceFaceDetector
{
    private readonly HttpClient _httpClient;
    private readonly string? _internalToken;
    private readonly ILogger<AiServiceFaceDetector> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AiServiceFaceDetector(
        HttpClient httpClient, IConfiguration config, ILogger<AiServiceFaceDetector> logger)
    {
        _httpClient = httpClient;
        _internalToken = config["Internal:Token"];   // /face-detect gate bằng X-Internal-Token (GEN-7)
        _logger = logger;
    }

    private record FaceDetectApiResponse(int FaceCount, List<string>? Signals);

    public async Task<FaceDetectResult> DetectAsync(string imageKey, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/face-detect")
        {
            Content = JsonContent.Create(new { imageKey })
        };
        msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

        var started = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(msg, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "AIService /face-detect HẾT GIỜ sau {Elapsed}ms (timeout client {Timeout}s)",
                started.ElapsedMilliseconds, _httpClient.Timeout.TotalSeconds);
            throw new AiServiceException("AIService /face-detect hết giờ", ex) { IsTimeout = true };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex,
                "Không gọi được AIService /face-detect sau {Elapsed}ms", started.ElapsedMilliseconds);
            throw new AiServiceException("Không gọi được AIService /face-detect", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                // Bucket lệch (BF ops) lộ ra ở đây: 404 kèm tên bucket trong body — log nguyên văn
                // để soi trực tiếp trên log Interview, không cần vào container AIService.
                _logger.LogError(
                    "AIService /face-detect TRẢ LỖI {StatusCode} sau {Elapsed}ms - {Error}",
                    (int)response.StatusCode, started.ElapsedMilliseconds, error);
                throw new AiServiceException(
                    $"AIService /face-detect trả {(int)response.StatusCode}: {error}");
            }

            FaceDetectApiResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<FaceDetectApiResponse>(Json, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "AIService /face-detect trả JSON không hợp lệ");
                throw new AiServiceException("AIService /face-detect trả JSON không hợp lệ", ex);
            }

            if (body is null)
                throw new AiServiceException("AIService /face-detect trả body rỗng");

            return new FaceDetectResult(body.FaceCount, body.Signals ?? new List<string>());
        }
    }
}
