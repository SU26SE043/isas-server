using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        private readonly TimeSpan _detectTimeout;
        private readonly ILogger<AiServiceFaceVerifyClient> _logger;

        // AC2 — trần thời gian RIÊNG cho /face-detect ở face-enroll. HttpClient dùng chung với /face-verify
        // giữ Timeout mặc định 100s; ứng viên đứng chờ ở cửa enroll thì 100s là quá lâu, mà hết giờ ở đây
        // không chặn ai (controller mở cửa — SEC-5). 15s chừa lần nạp FaceAnalysis lười đầu tiên (vài giây).
        internal const int DefaultDetectTimeoutSeconds = 15;

        public AiServiceFaceVerifyClient(HttpClient http, IConfiguration config, ILogger<AiServiceFaceVerifyClient> logger)
        {
            _http = http;
            // GEN-7: /face-verify nay gate X-Internal-Token (fail-closed) → đính token như CampaignSessionClient.
            _internalToken = config["Internal:Token"];
            var seconds = int.TryParse(config["AiService:FaceDetectTimeoutSeconds"], out var s) && s > 0
                ? s : DefaultDetectTimeoutSeconds;
            _detectTimeout = TimeSpan.FromSeconds(seconds);
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
                    await ThrowIfImageUnreadableAsync(resp, "/face-verify", ct);
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
            catch (ImageUnreadableException)
            {
                throw;   // lỗi DỮ LIỆU — không được bị khối dưới đổi thành "lỗi hạ tầng"
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gọi AIService /face-verify lỗi hạ tầng.");
                throw new DownstreamServiceException("Không gọi được AIService face-verify.", ex);
            }
        }

        // AC2 — face-enroll hỏi AIService ảnh mốc vừa upload có ĐÚNG 1 khuôn mặt không. Body camelCase
        // `{ imageKey }` (JsonContent.Create dùng web defaults; khoá đã khoá bằng test hợp đồng — lệch tên
        // khoá thì FastAPI trả 422 ⇒ controller mở cửa ⇒ cửa kiểm chết câm, không test nào khác kêu).
        public async Task<FaceDetectResult> DetectAsync(string imageKey, CancellationToken ct = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_detectTimeout);
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/face-detect")
                {
                    Content = JsonContent.Create(new { imageKey })
                };
                msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);
                using var resp = await _http.SendAsync(msg, timeout.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    await ThrowIfImageUnreadableAsync(resp, "/face-detect", timeout.Token);
                    _logger.LogWarning("AIService /face-detect → {Status} sau {Elapsed}ms",
                        resp.StatusCode, started.ElapsedMilliseconds);
                    throw new DownstreamServiceException(
                        $"AIService face-detect trả về {(int)resp.StatusCode}.");
                }

                var body = await resp.Content.ReadFromJsonAsync<DetectResponseDto>(cancellationToken: timeout.Token)
                    ?? throw new DownstreamServiceException("AIService face-detect trả về body rỗng.");

                var signals = (body.Signals ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToList();
                return new FaceDetectResult(body.FaceCount, signals);
            }
            catch (DownstreamServiceException)
            {
                throw;
            }
            catch (ImageUnreadableException)
            {
                throw;   // ảnh rác KHÔNG phải lỗi hạ tầng — đổi thành Downstream là controller mở cửa nhận mốc rác
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // người gọi bỏ đi — không phải lỗi hạ tầng, không được đổi thành "mở cửa"
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "AIService /face-detect HẾT GIỜ sau {Elapsed}ms (trần {Timeout}s).",
                    started.ElapsedMilliseconds, _detectTimeout.TotalSeconds);
                throw new DownstreamServiceException("AIService face-detect hết giờ.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gọi AIService /face-detect lỗi hạ tầng.");
                throw new DownstreamServiceException("Không gọi được AIService face-detect.", ex);
            }
        }

        // Mã AIService đặt trong body 422 khi ảnh không giải mã được (app/face_verify.py IMAGE_UNREADABLE_CODE).
        internal const string ImageUnreadableCode = "IMAGE_UNREADABLE";

        // 422 có HAI nghĩa ở AIService: (1) ảnh không giải mã được — `detail` là OBJECT mang mã
        // IMAGE_UNREADABLE; (2) pydantic từ chối body — `detail` là MẢNG, tức khoá JSON phía ta lệch hợp đồng.
        // CHỈ (1) được đổi thành ImageUnreadableException. (2) rơi xuống nhánh lỗi hạ tầng như cũ: coi nó là
        // "ảnh hỏng" thì một lần đổi tên khoá là face-enroll từ chối MỌI ứng viên (fail-closed toàn hệ).
        private async Task ThrowIfImageUnreadableAsync(HttpResponseMessage resp, string endpoint, CancellationToken ct)
        {
            if (resp.StatusCode != HttpStatusCode.UnprocessableEntity) return;
            var image = TryReadUnreadableImage(await resp.Content.ReadAsStringAsync(ct));
            if (image is null) return;
            _logger.LogWarning("AIService {Endpoint} → 422 IMAGE_UNREADABLE (ảnh '{Image}' không giải mã được).",
                endpoint, image);
            throw new ImageUnreadableException(image);
        }

        /// <summary>Tên ảnh hỏng nếu body là 422 IMAGE_UNREADABLE, ngược lại <c>null</c>.</summary>
        internal static string? TryReadUnreadableImage(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("detail", out var detail)
                    || detail.ValueKind != JsonValueKind.Object)
                    return null;
                if (!detail.TryGetProperty("code", out var code)
                    || code.ValueKind != JsonValueKind.String
                    || !string.Equals(code.GetString(), ImageUnreadableCode, StringComparison.Ordinal))
                    return null;
                return detail.TryGetProperty("image", out var image)
                       && image.ValueKind == JsonValueKind.String
                       && !string.IsNullOrWhiteSpace(image.GetString())
                    ? image.GetString()!.Trim()
                    : "image";
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private sealed record ResponseDto(int FaceCount, bool Match, float Score, List<string>? Signals);
        private sealed record DetectResponseDto(int FaceCount, List<string>? Signals);
    }
}
