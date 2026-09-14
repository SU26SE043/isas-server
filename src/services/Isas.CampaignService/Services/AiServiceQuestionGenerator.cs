using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// F9 — typed HttpClient gọi AIService POST /api/v1/generate-questions (đồng bộ, qua AiService:BaseUrl).
    /// Endpoint này B2C đã dùng sẵn (Isas.InterviewService/Services/AiServiceQuestionGenerator.cs) — bản này
    /// là phía Campaign (B2B), chỉ gửi jdText (B2B không có CV của một ứng viên cụ thể lúc soạn đề).
    ///
    /// CMP2-BE1 — kèm <c>criteriaContext</c>: bộ tiêu chí chấm của chiến dịch, gửi làm BỐI CẢNH để
    /// prompt biết buổi này sẽ được chấm bằng thước nào. Rỗng ⇒ khoá không ra dây ⇒ prompt nguyên xi.
    ///
    /// Response AIService: {"questions": ["câu 1", "câu 2", ...]} (mảng string thuần).
    /// Lỗi transport/timeout hoặc non-2xx → <see cref="DownstreamServiceException"/> → controller map 502
    /// (KHÔNG nuốt thành 400: lỗi upstream không phải lỗi request của HR — tiền lệ commit b1239d4 bên Interview).
    /// GEN-4: AIService không ghi DB — Campaign nhận kết quả rồi tự lưu.
    /// </summary>
    public class AiServiceQuestionGenerator : IQuestionGenerator
    {
        private static readonly JsonSerializerOptions CamelCase =
            new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly HttpClient _http;
        private readonly string? _internalToken;
        private readonly ILogger<AiServiceQuestionGenerator> _logger;

        public AiServiceQuestionGenerator(
            HttpClient http, IConfiguration config, ILogger<AiServiceQuestionGenerator> logger)
        {
            _http = http;
            // GEN-7: /generate-questions nay gate X-Internal-Token (fail-closed). Bản Interview của
            // client này (Isas.InterviewService/Services/AiServiceQuestionGenerator.cs) đã đính token
            // từ trước; bản Campaign thì chưa — đó là bất đối xứng gây ra lỗ Q2.
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        public Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, CancellationToken ct = default)
            => GenerateAsync(jobCategory, jdText, count, "Junior", ct);

        public Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, string seniority, CancellationToken ct)
            // CMP2-BE1 — không có bối cảnh tiêu chí ⇒ mảng rỗng ⇒ khoá `criteriaContext` ra dây là
            // `null` ⇒ prompt AIService GIỮ NGUYÊN XI. Đây là đường của mọi caller cũ.
            => GenerateAsync(jobCategory, jdText, count, seniority,
                Array.Empty<QuestionCriterionContext>(), ct);

        public async Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, string seniority,
            IReadOnlyList<QuestionCriterionContext> criteriaContext, CancellationToken ct)
            // SC2 · W2 — không có tiêu chí để gắn nhãn ⇒ khoá `criteria` không ra dây, nhãn trả về null
            // ⇒ caller cũ nhận đúng danh sách chuỗi như trước.
            => (await GenerateAsync(jobCategory, jdText, count, seniority, criteriaContext,
                    Array.Empty<QuestionCriterionRef>(), ct))
                .Select(q => q.Text)
                .ToList();

        public async Task<List<GeneratedQuestion>> GenerateAsync(
            string jobCategory, string? jdText, int? count, string seniority,
            IReadOnlyList<QuestionCriterionContext> criteriaContext,
            IReadOnlyList<QuestionCriterionRef> criteria, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                // cvText = null: B2B soạn đề chung cho cả chiến dịch (mọi ứng viên nhận cùng seed — E1 fairness),
                // nên không có CV cá nhân nào để cá nhân hoá. count null → AIService giữ mặc định của nó.
                //
                // SEN1 — `seniority`: tên thành viên ở đây là nơi duy nhất quyết định tên khoá ra dây,
                // và lệch tên với pydantic thì KHÔNG ném lỗi ở đâu cả — field im lặng biến mất.
                //
                // ⚠ Đã probe thật: `JsonContent.Create` dùng `JsonSerializerDefaults.Web` nên CÓ áp
                // camelCase ⇒ rủi ro là đổi TÊN (`seniorityLevel`), không phải hoa/thường.
                //
                // Không để rỗng/null ra dây: `GenerateQuestionsRequest.seniority` bên Python khai `str`
                // (không Optional) ⇒ `null` là 422, tức HR bấm "sinh câu hỏi" nhận 502 mà nguyên nhân
                // thật nằm ở một field phụ.
                //
                // CMP2-BE1 — BỐI CẢNH thước đo. Rỗng ⇒ gửi `null` chứ không phải `[]`: bên Python
                // `criteriaContext` khai `list[...] | None`, và khối prompt rẽ nhánh theo truthiness —
                // hai giá trị này cho cùng kết quả, nhưng `null` nói đúng ý "chiến dịch chưa khai
                // tiêu chí" thay vì "khai một bộ rỗng".
                //
                // ⚠ Tên khoá `criteriaContext` phải KHỚP TỪNG CHỮ với field pydantic. Lệch tên KHÔNG
                // ném lỗi ở đâu cả: `GenerateQuestionsRequest` không set `model_config` nên pydantic
                // `extra='ignore'` NUỐT IM LẶNG — .NET vẫn gửi, HTTP vẫn 200, prompt chỉ đơn giản
                // không đổi một chữ. Lớp bug này đã cắn repo bốn lần (`focusCriteria`/BC14 ·
                // `metricsVersion` · `adaptiveMaxQuestions` · `transcriptEngine`).
                //
                // ⚠ KHÔNG dùng lại khoá `criteria` sẵn có: khoá đó là đường GẮN NHÃN
                // (targetCriterionIds) và nó kéo theo ràng buộc PHÂN BỔ BẮT BUỘC của SC1 — đúng thứ
                // đợt này cố ý chưa làm (xem docblock `IQuestionGenerator`).
                var contextPayload = criteriaContext is { Count: > 0 }
                    ? criteriaContext
                        .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                        .Select(c => new { name = c.Name.Trim(), description = c.Description?.Trim() })
                        .ToArray()
                    : null;
                if (contextPayload is { Length: 0 })
                    contextPayload = null;

                // SC2 · W2 — đường GẮN NHÃN: CHỈ tiêu chí WhenTargeted, mỗi dòng mang `criterionId` để
                // AIService trả `targetCriteria` theo đúng id đã cấp (và drop id lạ — `_keep_known_ids`).
                // Rỗng ⇒ khoá `criteria` KHÔNG RA DÂY (hợp đồng W2: "0 tiêu chí ⇒ không gửi khoá, y như
                // hôm nay") — vì thế payload dựng bằng Dictionary thay vì anonymous object: JsonContent
                // với Web defaults GHI `null` chứ không bỏ khoá, mà test CMP2 khoá "không có khoá
                // `criteria`" bằng EnumerateObject. Python xử `if criteria: … elif criteriaContext:` nên
                // gửi CẢ HAI không in trùng.
                //
                // ⚠ Khoá `criteria`/`criterionId`/`name`/`description` phải KHỚP TỪNG CHỮ với
                // `CriterionRef` trong schemas.py — lệch tên là pydantic nuốt im lặng: HTTP 200, nhãn
                // không bao giờ về, mọi câu B2B quay lại bị chấm trên cả bộ (lớp bug đã cắn repo 4 lần).
                // `ToString("D")`: Guid chữ thường có gạch — chuỗi id đúng như Campaign sẽ parse lại.
                var criteriaPayload = criteria is { Count: > 0 }
                    ? criteria
                        .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                        .Select(c => new
                        {
                            criterionId = c.CriterionId.ToString("D"),
                            name = c.Name.Trim(),
                            description = string.IsNullOrWhiteSpace(c.Description) ? null : c.Description!.Trim()
                        })
                        .ToArray()
                    : null;
                if (criteriaPayload is { Length: 0 })
                    criteriaPayload = null;

                // Khoá cấp một viết TƯỜNG MINH (Dictionary, không anonymous type): tên khoá là hợp đồng
                // dây, và Dictionary cho phép BỎ HẲN `criteria` khi không có tiêu chí nhắm được.
                var payload = new Dictionary<string, object?>
                {
                    ["jobCategory"] = jobCategory,
                    ["cvText"] = null,
                    ["jdText"] = jdText,
                    ["count"] = count,
                    ["seniority"] = string.IsNullOrWhiteSpace(seniority) ? "Junior" : seniority,
                    ["criteriaContext"] = contextPayload,
                };
                if (criteriaPayload is not null)
                    payload["criteria"] = criteriaPayload;

                using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/generate-questions")
                {
                    Content = JsonContent.Create(payload)
                };
                msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);
                resp = await _http.SendAsync(msg, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogError(ex, "Không gọi được AIService /generate-questions");
                throw new DownstreamServiceException("Không gọi được AIService /generate-questions.", ex);
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("AIService /generate-questions → {Status}", resp.StatusCode);
                throw new DownstreamServiceException(
                    $"AIService /generate-questions trả về {(int)resp.StatusCode}.");
            }

            ResponseDto? body;
            try
            {
                body = await resp.Content.ReadFromJsonAsync<ResponseDto>(CamelCase, ct);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException)
            {
                // Body không parse được = hợp đồng upstream vỡ → vẫn là lỗi upstream (502), không phải 400.
                _logger.LogError(ex, "AIService /generate-questions trả body không đọc được");
                throw new DownstreamServiceException("AIService /generate-questions trả về body không hợp lệ.", ex);
            }

            return AlignTargets(body?.Questions, body?.TargetCriteria, _logger);
        }

        /// <summary>
        /// SC2 · W2 — ghép `questions[i]` với `targetCriteria[i]` TRƯỚC khi lọc câu rỗng: lọc trước rồi
        /// ghép sau là lệch index im lặng (câu 2 nhận nhãn của câu 3). Luật:
        /// <list type="bullet">
        /// <item><c>targetCriteria</c> vắng/null ⇒ mọi câu nhãn <c>null</c> (chấm đủ bộ).</item>
        /// <item>độ dài LỆCH với <c>questions</c> ⇒ BỎ NHÃN CẢ BATCH + LogWarning — không gán "theo index có
        /// sẵn" (nửa đầu đúng nửa sau sai còn tệ hơn không có nhãn), không 500 (câu hỏi vẫn dùng được).</item>
        /// <item>phần tử i là <c>[]</c> ⇒ giữ <c>[]</c> (I2: đã xét, không nhắm); chuỗi không phải GUID ⇒ bỏ
        /// chuỗi đó; trùng ⇒ bỏ trùng, giữ thứ tự.</item>
        /// </list>
        /// </summary>
        internal static List<GeneratedQuestion> AlignTargets(
            List<string>? questions, List<List<string>?>? targetCriteria, ILogger logger)
        {
            var raw = questions ?? new List<string>();
            List<List<string>?>? targets = targetCriteria;
            if (targets is not null && targets.Count != raw.Count)
            {
                logger.LogWarning(
                    "SC2/W2 — AIService trả targetCriteria ({Targets}) lệch độ dài với questions ({Questions}) → bỏ nhãn cả lượt.",
                    targets.Count, raw.Count);
                targets = null;
            }

            var result = new List<GeneratedQuestion>(raw.Count);
            for (var i = 0; i < raw.Count; i++)
            {
                var text = raw[i];
                if (string.IsNullOrWhiteSpace(text)) continue;

                IReadOnlyList<Guid>? ids = null;
                if (targets is not null)
                {
                    var list = new List<Guid>();
                    foreach (var s in targets[i] ?? new List<string>())
                    {
                        if (Guid.TryParse(s, out var g) && !list.Contains(g))
                            list.Add(g);
                    }
                    ids = list;   // [] giữ [] — KHÔNG gộp về null
                }
                result.Add(new GeneratedQuestion(text.Trim(), ids));
            }
            return result;
        }

        // `TargetCriteria` khai `List<List<string>?>?`: phần tử null (AIService không nên trả, nhưng
        // JSON cho phép) coi như [] chứ không làm vỡ deserialize cả body.
        private sealed record ResponseDto(List<string>? Questions, List<List<string>?>? TargetCriteria);
    }
}
