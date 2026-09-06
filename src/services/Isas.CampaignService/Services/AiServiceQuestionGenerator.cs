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

                using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/generate-questions")
                {
                    Content = JsonContent.Create(new
                    {
                        jobCategory,
                        cvText = (string?)null,
                        jdText,
                        count,
                        seniority = string.IsNullOrWhiteSpace(seniority) ? "Junior" : seniority,
                        criteriaContext = contextPayload
                    })
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

            return (body?.Questions ?? new List<string>())
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q.Trim())
                .ToList();
        }

        private sealed record ResponseDto(List<string>? Questions);
    }
}
