using System.Net.Http.Json;
using System.Text.Json;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// D2 — typed HttpClient gọi InterviewService POST /internal/sessions/campaign (máy-máy,
    /// X-Internal-Token gắn trong client, KHÔNG qua gateway). Interview trả PracticeSessionResponse
    /// (create-or-get idempotent theo candidate+campaign) → map ra sessionId + câu hỏi. Lỗi hạ tầng →
    /// DownstreamServiceException (502).
    /// </summary>
    public class CampaignSessionClient : ICampaignSessionClient
    {
        private readonly HttpClient _http;
        private readonly string? _internalToken;
        private readonly ILogger<CampaignSessionClient> _logger;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public CampaignSessionClient(HttpClient http, IConfiguration config, ILogger<CampaignSessionClient> logger)
        {
            _http = http;
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        // Shape khớp Interview PracticeSessionResponse (chỉ field cần: id + questions[]).
        private record SessionApiResponse(Guid Id, List<QuestionApiResponse>? Questions);
        private record QuestionApiResponse(Guid Id, int OrderNo, string Content, int TimeLimitSec);

        public async Task<CampaignSessionResult> CreateOrGetSessionAsync(
            Guid candidateId, Guid campaignId, string jobCategory,
            IReadOnlyList<string> questions, IReadOnlyList<SessionCriterionInput> criteria,
            DateTime? expiresAt = null,
            CancellationToken ct = default)
        {
            var payload = new
            {
                candidateId,
                campaignId,
                jobCategory,
                questions,
                criteria = criteria.Select(c => new { c.Name, c.Description, c.Weight, c.MaxScore }),
                expiresAt   // BK18 — Interview map → session.Deadline (I2); null = không hard-deadline
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/sessions/campaign")
            {
                Content = JsonContent.Create(payload)
            };
            msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(msg, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogError(ex, "Không gọi được InterviewService /internal/sessions/campaign");
                throw new DownstreamServiceException("Không gọi được InterviewService (create-or-get session)", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("InterviewService create-or-get session lỗi: {StatusCode} - {Error}", response.StatusCode, error);
                throw new DownstreamServiceException($"InterviewService create-or-get session trả {(int)response.StatusCode}");
            }

            SessionApiResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<SessionApiResponse>(Json, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "InterviewService create-or-get session trả JSON không hợp lệ");
                throw new DownstreamServiceException("InterviewService create-or-get session trả JSON không hợp lệ", ex);
            }

            if (body is null || body.Id == Guid.Empty)
                throw new DownstreamServiceException("InterviewService create-or-get session trả rỗng");

            var mapped = (body.Questions ?? new List<QuestionApiResponse>())
                .Select(q => new SessionQuestion(q.Id, q.OrderNo, q.Content, q.TimeLimitSec))
                .ToList();

            return new CampaignSessionResult(body.Id, mapped);
        }
    }
}
