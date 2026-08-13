using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Isas.CampaignService.Services
{
    public record CreditReservationResult(Guid ReservationId, int ReservedCredits);

    public interface ICreditReservationClient
    {
        /// <summary>
        /// Giữ 1 credit ví ORG. <paramref name="operationId"/> là khoá idempotency phía Payment (cột
        /// <c>session_id</c>). 402 → <see cref="InsufficientOrgCreditException"/>; lỗi hạ tầng →
        /// <see cref="DownstreamServiceException"/>.
        /// </summary>
        Task<CreditReservationResult> ReserveAsync(
            string ownerType, Guid ownerId, Guid operationId, CancellationToken ct = default);

        Task ConsumeAsync(Guid operationId, CancellationToken ct = default);
        Task ReleaseAsync(Guid operationId, CancellationToken ct = default);
    }

    /// <summary>
    /// CAMP-19 — Campaign giữ/trừ credit ví Org cho lượt CHẤM THỬ tính phí. Sao mẫu
    /// <c>InterviewService/Services/CreditReservationClient</c> (máy-máy, X-Internal-Token, không qua gateway).
    ///
    /// <para><b>PaymentService KHÔNG phải sửa gì.</b> Trường <c>sessionId</c> bên đó chỉ là khoá
    /// idempotency chứ không phải tham chiếu buổi thi — <c>CvAnalysisService</c> đã dùng đúng như vậy
    /// cho một thao tác không-session từ BC7b. Ở đây khoá là <c>rubric_preview_runs.id</c>.</para>
    /// </summary>
    public class CreditReservationClient : ICreditReservationClient
    {
        private readonly HttpClient _http;
        private readonly string? _internalToken;
        private readonly ILogger<CreditReservationClient> _logger;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public CreditReservationClient(
            HttpClient http, IConfiguration config, ILogger<CreditReservationClient> logger)
        {
            _http = http;
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        private record ReserveApiResponse(Guid ReservationId, int ReservedCredits);

        public async Task<CreditReservationResult> ReserveAsync(
            string ownerType, Guid ownerId, Guid operationId, CancellationToken ct = default)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/credits/reserve")
            {
                Content = JsonContent.Create(new { ownerType, ownerId, sessionId = operationId })
            };
            msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(msg, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogError(ex, "Không gọi được PaymentService /internal/credits/reserve");
                throw new DownstreamServiceException("Không gọi được PaymentService (reserve credit)", ex);
            }

            // 402 = ví org hết credit / chạm hạn mức → KHÔNG chạy chấm thử (PAY-5).
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
                throw new InsufficientOrgCreditException("Tổ chức không đủ credit để chạy chấm thử.");

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("PaymentService reserve lỗi: {StatusCode} - {Error}", response.StatusCode, error);
                throw new DownstreamServiceException($"PaymentService reserve trả {(int)response.StatusCode}");
            }

            ReserveApiResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<ReserveApiResponse>(Json, ct);
            }
            catch (JsonException ex)
            {
                throw new DownstreamServiceException("PaymentService reserve trả JSON không hợp lệ", ex);
            }

            if (body is null)
                throw new DownstreamServiceException("PaymentService reserve trả rỗng");

            return new CreditReservationResult(body.ReservationId, body.ReservedCredits);
        }

        public Task ConsumeAsync(Guid operationId, CancellationToken ct = default)
            => PostCreditOpAsync("/internal/credits/consume", operationId, ct);

        public Task ReleaseAsync(Guid operationId, CancellationToken ct = default)
            => PostCreditOpAsync("/internal/credits/release", operationId, ct);

        // owner lấy từ reservation phía Payment nên không gửi. consume/release absorbing (PAY-11:
        // Payment trả 200 mọi outcome) ⇒ mọi non-2xx đều là lỗi hạ tầng.
        private async Task PostCreditOpAsync(string path, Guid operationId, CancellationToken ct)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new { sessionId = operationId })
            };
            msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(msg, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogError(ex, "Không gọi được PaymentService {Path}", path);
                throw new DownstreamServiceException($"Không gọi được PaymentService {path}", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("PaymentService {Path} lỗi: {StatusCode} - {Error}", path, response.StatusCode, error);
                throw new DownstreamServiceException($"PaymentService {path} trả {(int)response.StatusCode}");
            }
        }
    }
}
