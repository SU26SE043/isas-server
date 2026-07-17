using System.Net.Http.Json;
using System.Text.Json;

namespace Isas.PaymentService.Services
{
    // DB18 — typed HttpClient Payment→Interview `/internal/sessions/exists` (máy-máy, X-Internal-Token,
    // KHÔNG qua gateway). Nhái mẫu chiều-gọi-nội-bộ CreditReservationClient (Interview→Payment). BaseUrl
    // cấu hình ở Program.cs từ `Interview:BaseUrl`. MỌI lỗi (hạ tầng/non-2xx/JSON hỏng) → NÉM
    // InterviewServiceException để reconciler skip vòng (KHÔNG release oan khi không xác minh được).
    public class InterviewSessionClient : IInterviewSessionClient
    {
        private readonly HttpClient _httpClient;
        private readonly string? _internalToken;
        private readonly ILogger<InterviewSessionClient> _logger;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public InterviewSessionClient(
            HttpClient httpClient, IConfiguration config, ILogger<InterviewSessionClient> logger)
        {
            _httpClient = httpClient;
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        // Res 200: { existingIds: Guid[] } (Interview SessionExistsResponse).
        private record ExistsApiResponse(List<Guid>? ExistingIds);

        public async Task<HashSet<Guid>> GetExistingSessionsAsync(
            IReadOnlyList<Guid> sessionIds, CancellationToken ct = default)
        {
            if (sessionIds is null || sessionIds.Count == 0)
                return new HashSet<Guid>();

            // SessionExistsRequest { sessionIds }.
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/sessions/exists")
            {
                Content = JsonContent.Create(new { sessionIds })
            };
            msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(msg, ct);
            }
            // InvalidOperationException = BaseAddress chưa set (Interview:BaseUrl trống) + URI tương đối →
            // vẫn NÉM (reconciler skip vòng, không release oan).
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogError(ex, "Không gọi được InterviewService /internal/sessions/exists");
                throw new InterviewServiceException("Không gọi được InterviewService /internal/sessions/exists", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("InterviewService exists lỗi: {StatusCode} - {Error}", response.StatusCode, error);
                throw new InterviewServiceException($"InterviewService exists trả {(int)response.StatusCode}");
            }

            ExistsApiResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<ExistsApiResponse>(Json, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "InterviewService exists trả JSON không hợp lệ");
                throw new InterviewServiceException("InterviewService exists trả JSON không hợp lệ", ex);
            }

            if (body is null)
                throw new InterviewServiceException("InterviewService exists trả rỗng");

            return body.ExistingIds is null ? new HashSet<Guid>() : body.ExistingIds.ToHashSet();
        }
    }
}
