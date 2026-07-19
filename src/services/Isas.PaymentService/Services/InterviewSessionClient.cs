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

        // Res 200: { existingIds: Guid[], states?: [{ sessionId, status }] } (Interview SessionExistsResponse).
        // `states` VẮNG khi Interview còn image trước R1 → parse thành rỗng (KHÔNG suy ra từ existingIds).
        private record StateApiDto(Guid SessionId, string? Status);
        private record ExistsApiResponse(List<Guid>? ExistingIds, List<StateApiDto>? States);

        public async Task<InterviewSessionsSnapshot> GetExistingSessionsAsync(
            IReadOnlyList<Guid> sessionIds, CancellationToken ct = default)
        {
            if (sessionIds is null || sessionIds.Count == 0)
                return new InterviewSessionsSnapshot(
                    new HashSet<Guid>(), new Dictionary<Guid, string>());

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

            // TỒN TẠI đọc từ existingIds (trường Interview bản CŨ vẫn điền) — KHÔNG suy từ states.
            var existing = body.ExistingIds is null ? new HashSet<Guid>() : body.ExistingIds.ToHashSet();

            // states VẮNG (Interview cũ) → dictionary RỖNG ⇒ reconciler SKIP mọi session đang tồn tại =
            // đúng bằng hành vi trước R1. Bỏ qua entry status rỗng/null: "không biết" ≠ "trạng thái nào đó".
            var states = new Dictionary<Guid, string>();
            if (body.States is not null)
            {
                foreach (var s in body.States)
                {
                    if (!string.IsNullOrWhiteSpace(s.Status))
                        states[s.SessionId] = s.Status!;
                }
            }

            return new InterviewSessionsSnapshot(existing, states);
        }
    }
}
