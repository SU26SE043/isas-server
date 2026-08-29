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
            int? maxDeepPerQuestion = null, string seniority = "Junior", int rubricVersion = 1,
            IReadOnlyList<SessionQuestionInput>? questionDetails = null,
            CampaignScoringPolicyInput? scoringPolicy = null, CancellationToken ct = default)
            => await CreateOrGetSessionAsync(candidateId, campaignId, orgId, jobCategory, questions, criteria, expiresAt, adaptiveEnabled, maxFollowUps, maxQuestions, maxDeepPerQuestion, "vi", seniority, rubricVersion, questionDetails, scoringPolicy, ct);

        public async Task<CampaignSessionResult> CreateOrGetSessionAsync(Guid candidateId, Guid campaignId, Guid orgId, string jobCategory, IReadOnlyList<string> questions, IReadOnlyList<SessionCriterionInput> criteria, DateTime? expiresAt, bool? adaptiveEnabled, int? maxFollowUps, int? maxQuestions, int? maxDeepPerQuestion, string language, string seniority, int rubricVersion, IReadOnlyList<SessionQuestionInput>? questionDetails, CampaignScoringPolicyInput? scoringPolicy, CancellationToken ct)
        {
            var payload = new
            {
                candidateId,
                campaignId,
                orgId,      // BK14 — Interview reserve credit owner=Org theo id này (PAY-6)
                jobCategory,
                questions,
                // CAMP-16 — `levels` gửi kèm; rỗng = chưa khai mốc ⇒ Interview dùng dải mặc định như
                // trước tính năng này (hai service deploy không nguyên tử, bản Interview cũ bỏ qua field).
                criteria = criteria.Select(c => new
                {
                    c.Name, c.Description, c.Weight, c.MaxScore,
                    levels = c.Levels.Select(l => new { l.Score, l.Descriptor })
                }),
                expiresAt,  // BK18 — Interview map → session.Deadline (I2); null = không hard-deadline
                // INT-17 — Interview đóng dấu lên practice_sessions lúc tạo; null = tắt/mặc định.
                adaptiveEnabled,
                maxFollowUps,
                maxQuestions,
                // INT-17b — >0 ⇒ mỗi câu campaign mọc chuỗi đào sâu xen kẽ ngay sau nó.
                maxDeepPerQuestion
                ,language,
                seniority,
                // CAMP-18 — Interview ghim số này lên buổi thi; vắng thì bên đó dùng 1 (= mọi row đang có).
                rubricVersion,
                // Câu hỏi KÈM đáp án mẫu. Gửi SONG SONG với `questions` chứ không thay thế: hai service
                // deploy không nguyên tử, nên bản Interview cũ (chưa biết field này) vẫn phải chạy được.
                // Bản Interview mới ưu tiên field này và bỏ qua nếu số lượng lệch với `questions`.
                questionDetails = questionDetails?.Select(q => new { q.Text, q.SampleAnswer }),
                // SCP1 · B5 — hợp đồng chấm điểm (chính sách biểu thức). Interview ghim CẢ 4 vào
                // practice_sessions; null (campaign chưa áp chính sách) ⇒ bên đó dùng weighted mặc định.
                campaignPolicyVersion = scoringPolicy?.Version,
                campaignPolicyExpression = scoringPolicy?.Expression,
                campaignPolicyPassScorePct = scoringPolicy?.PassScorePct,
                campaignPolicyEngineVersion = scoringPolicy?.EngineVersion
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
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("InterviewService create-or-get session: quá tải phiên chạy (429) - {Error}", error);
                    throw new CampaignInterviewCapacityExceededException("Hệ thống đang đạt giới hạn phiên phỏng vấn đồng thời. Vui lòng thử lại sau.");
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
        // CriterionName/MaxScore: Interview trả kèm để HR đọc được tên tiêu chí (id không tra ngược được
        // sang campaign_criteria). Nullable — buổi chấm cũ không có.
        private record TranscriptApiScore(Guid CriterionId, decimal Score, string? Reasoning,
            string? CriterionName = null, int? MaxScore = null);

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
                            CriterionName = s.CriterionName,
                            Score = s.Score,
                            MaxScore = s.MaxScore,
                            Reasoning = s.Reasoning
                        })
                        .ToList()
                })
                .ToList();

            return new SessionTranscriptResponse { SessionId = sessionId, Questions = questions };
        }

        // CAMP-20 — shape khớp hợp đồng GET /internal/rubrics/b2c. KHÔNG có `id` (id Interview vô nghĩa
        // với Campaign) và KHÔNG có `scoringScope` (Campaign không có cột, đường chấm B2B không đọc).
        // Field lạ bị bỏ qua (case-insensitive) ⇒ Interview thêm field mới không làm vỡ bên này.
        private record B2CRubricApiResponse(
            string? JobCategory, string? Language, int Version, List<B2CRubricApiCriterion>? Criteria);
        private record B2CRubricApiCriterion(
            string? Name, string? Description, decimal Weight, int MaxScore, List<B2CRubricApiLevel>? Levels);
        private record B2CRubricApiLevel(int Score, string? Descriptor);

        public async Task<B2CRubricResponse> GetB2CRubricAsync(
            string jobCategory, string language, CancellationToken ct = default)
        {
            using var msg = new HttpRequestMessage(
                HttpMethod.Get,
                $"/internal/rubrics/b2c?jobCategory={Uri.EscapeDataString(jobCategory)}" +
                $"&language={Uri.EscapeDataString(language)}");
            msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(msg, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogError(ex, "Không gọi được InterviewService /internal/rubrics/b2c");
                throw new DownstreamServiceException("Không gọi được InterviewService (bộ chuẩn B2C)", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                // 404 = admin CHƯA soạn bộ chuẩn cho tổ hợp này. Vẫn là DownstreamServiceException (→502
                // theo hợp đồng đã chốt với FE) nhưng THÔNG ĐIỆP phải nói đúng chuyện: HR đọc "lỗi hệ
                // thống" rồi báo sự cố, trong khi việc cần làm là bảo admin soạn bộ đó.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning(
                        "Chưa có bộ chuẩn B2C cho ({JobCategory}, {Language}) - {Error}",
                        jobCategory, language, error);
                    // Loại DẪN XUẤT của DownstreamServiceException: đường CHÉP vẫn ra 502 y như trước
                    // (catch theo lớp cơ sở), còn đường XEM TRƯỚC bắt loại này để trả 404 — "chưa ai
                    // soạn" là câu trả lời bình thường cho một câu hỏi "có sẵn không?", không phải sự cố.
                    throw new SystemRubricNotFoundException(
                        $"Chưa có bộ chuẩn cho ({jobCategory}, {language}) — quản trị viên cần soạn bộ này trước.");
                }
                _logger.LogError("InterviewService bộ chuẩn B2C lỗi: {StatusCode} - {Error}", response.StatusCode, error);
                throw new DownstreamServiceException($"InterviewService bộ chuẩn B2C trả {(int)response.StatusCode}");
            }

            B2CRubricApiResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<B2CRubricApiResponse>(Json, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "InterviewService bộ chuẩn B2C trả JSON không hợp lệ");
                throw new DownstreamServiceException("InterviewService bộ chuẩn B2C trả JSON không hợp lệ", ex);
            }

            // Bộ RỖNG là lỗi, không phải "bộ chuẩn không có tiêu chí nào": chép về sẽ xoá sạch tiêu chí
            // của chiến dịch rồi để lại một campaign không thước đo — Interview vẫn chấm ra điểm.
            if (body is null || body.Criteria is not { Count: > 0 })
                throw new DownstreamServiceException(
                    $"InterviewService trả bộ chuẩn rỗng cho ({jobCategory}, {language}).");

            var criteria = body.Criteria
                .Select(c => new B2CRubricCriterion(
                    c.Name ?? string.Empty,
                    c.Description,
                    c.Weight,
                    c.MaxScore,
                    (c.Levels ?? new List<B2CRubricApiLevel>())
                        // `.Include()` phía Interview không bảo đảm thứ tự mốc; sort tại đây để
                        // order_no/hiển thị không phụ thuộc thứ tự Postgres trả về.
                        .OrderBy(l => l.Score)
                        .Select(l => new B2CRubricLevel(l.Score, l.Descriptor ?? string.Empty))
                        .ToList()))
                .ToList();

            // Echo lại tham số đã HỎI, không lấy giá trị Interview trả về: nếu bên đó echo sai (hoặc
            // bản cũ chưa echo) thì audit/response sẽ ghi một tổ hợp khác với tổ hợp thật sự được chép.
            return new B2CRubricResponse(jobCategory, language, body.Version, criteria);
        }
    }
}
