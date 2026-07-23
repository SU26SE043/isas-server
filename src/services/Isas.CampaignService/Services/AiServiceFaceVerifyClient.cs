using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// SEC-2 — typed HttpClient gọi AIService POST /api/v1/face-verify (mirror <see cref="AiServiceCriteriaSuggester"/>).
    /// Body: { referenceImageKey, liveImageKey, threshold? }; response: { faceCount, match, score, signals[] }.
    /// Khác suggest-criteria (fallback null): face-verify lỗi → NÉM <see cref="DownstreamServiceException"/> để
    /// controller quyết (không lặng lẽ "khớp"). D13: cờ chỉ để HR xem, KHÔNG auto-chặn.
    /// </summary>
    public class AiServiceFaceVerifyClient : IAiServiceFaceVerifyClient
    {
        private readonly HttpClient _http;
        private readonly string? _internalToken;
        private readonly ILogger<AiServiceFaceVerifyClient> _logger;

        public AiServiceFaceVerifyClient(HttpClient http, IConfiguration config, ILogger<AiServiceFaceVerifyClient> logger)
        {
            _http = http;
            // GEN-7: /face-verify nay gate X-Internal-Token (fail-closed) → đính token như CampaignSessionClient.
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        public async Task<FaceVerifyResult> VerifyAsync(
            string referenceImageKey, string liveImageKey, double? threshold = null, CancellationToken ct = default)
        {
            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/face-verify")
                {
                    Content = JsonContent.Create(new { referenceImageKey, liveImageKey, threshold })
                };
                // X-Internal-Token gắn trong client, KHÔNG qua gateway (mirror CampaignSessionClient).
                msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);
                var resp = await _http.SendAsync(msg, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("AIService /face-verify → {Status}", resp.StatusCode);
                    throw new DownstreamServiceException(
                        $"AIService face-verify trả về {(int)resp.StatusCode}.");
                }

                var body = await resp.Content.ReadFromJsonAsync<ResponseDto>(cancellationToken: ct)
                    ?? throw new DownstreamServiceException("AIService face-verify trả về body rỗng.");

                var signals = (body.Signals ?? new List<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .ToList();

                return new FaceVerifyResult(body.FaceCount, body.Match, body.Score, signals);
            }
            catch (DownstreamServiceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gọi AIService /face-verify lỗi hạ tầng.");
                throw new DownstreamServiceException("Không gọi được AIService face-verify.", ex);
            }
        }

        private sealed record ResponseDto(int FaceCount, bool Match, float Score, List<string>? Signals);
    }
}
