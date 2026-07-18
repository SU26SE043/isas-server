using System.Net.Http.Json;
using System.Text.Json;
using Isas.CampaignService.DTOs;

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
            Guid candidateId, Guid campaignId, Guid orgId, string jobCategory,
            IReadOnlyList<string> questions, IReadOnlyList<SessionCriterionInput> criteria,
            DateTime? expiresAt = null,
            bool? adaptiveEnabled = null, int? maxFollowUps = null, int? maxQuestions = null,
            CancellationToken ct = default)
        {
            var payload = new
            {
                candidateId,
                campaignId,
                orgId,      // BK14 — Interview reserve credit owner=Org theo id này (PAY-6)
                jobCategory,
                questions,
                criteria = criteria.Select(c => new { c.Name, c.Description, c.Weight, c.MaxScore }),
                expiresAt,  // BK18 — Interview map → session.Deadline (I2); null = không hard-deadline
                // INT-17 — Interview đóng dấu lên practice_sessions lúc tạo; null = tắt/mặc định.
                adaptiveEnabled,
                maxFollowUps,
                maxQuestions
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
                // BK14 — 402 = ví org hết credit (reserve chặn, PAY-5) → map riêng thành
                // InsufficientOrgCreditException để controller trả 402 (không phải 502 như lỗi hạ tầng).
                if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
                {
                    _logger.LogWarning("InterviewService create-or-get session: ví org hết credit (402) - {Error}", error);
                    throw new InsufficientOrgCreditException("Tổ chức không đủ credit để bắt đầu phỏng vấn.");
                }
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

        // AI4 — shape khớp Interview QuestionResponse/AnswerResponse (chỉ field HR cần: câu hỏi + transcript
        // + per-criterion score/reasoning + needsReview). Unknown field bị bỏ qua (case-insensitive).
        private record TranscriptApiQuestion(
            Guid Id, int OrderNo, string Content, int TimeLimitSec, TranscriptApiAnswer? Answer);
        private record TranscriptApiAnswer(
            Guid Id, string Status, int DurationSec, string? Transcript,
            List<TranscriptApiScore>? Scores, bool NeedsReview);
        private record TranscriptApiScore(Guid CriterionId, decimal Score, string? Reasoning);

        public async Task<SessionTranscriptResponse> GetSessionTranscriptAsync(
            Guid sessionId, CancellationToken ct = default)
        {
            using var msg = new HttpRequestMessage(
                HttpMethod.Get, $"/internal/sessions/{sessionId}/answers");
            msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(msg, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogError(ex, "Không gọi được InterviewService /internal/sessions/{SessionId}/answers", sessionId);
                throw new DownstreamServiceException("Không gọi được InterviewService (transcript)", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("InterviewService transcript lỗi: {StatusCode} - {Error}", response.StatusCode, error);
                throw new DownstreamServiceException($"InterviewService transcript trả {(int)response.StatusCode}");
            }

            List<TranscriptApiQuestion>? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<List<TranscriptApiQuestion>>(Json, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "InterviewService transcript trả JSON không hợp lệ");
                throw new DownstreamServiceException("InterviewService transcript trả JSON không hợp lệ", ex);
            }

            var questions = (body ?? new List<TranscriptApiQuestion>())
                .Select(q => new TranscriptQuestion
                {
                    QuestionId = q.Id,
                    OrderNo = q.OrderNo,
                    Content = q.Content,
                    Transcript = q.Answer?.Transcript,
                    NeedsReview = q.Answer?.NeedsReview ?? false,
                    Scores = (q.Answer?.Scores ?? new List<TranscriptApiScore>())
                        .Select(s => new TranscriptCriterionScore
                        {
                            CriterionId = s.CriterionId,
                            Score = s.Score,
                            Reasoning = s.Reasoning
                        })
                        .ToList()
                })
                .ToList();

            return new SessionTranscriptResponse { SessionId = sessionId, Questions = questions };
        }
    }
}
