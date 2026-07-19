using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// F9 — typed HttpClient gọi AIService POST /api/v1/generate-questions (đồng bộ, qua AiService:BaseUrl).
    /// Endpoint này B2C đã dùng sẵn (Isas.InterviewService/Services/AiServiceQuestionGenerator.cs) — bản này
    /// là phía Campaign (B2B), chỉ gửi jdText (B2B không có CV của một ứng viên cụ thể lúc soạn đề).
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
        private readonly ILogger<AiServiceQuestionGenerator> _logger;

        public AiServiceQuestionGenerator(HttpClient http, ILogger<AiServiceQuestionGenerator> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, CancellationToken ct = default)
        {
            HttpResponseMessage resp;
            try
            {
                // cvText = null: B2B soạn đề chung cho cả chiến dịch (mọi ứng viên nhận cùng seed — E1 fairness),
                // nên không có CV cá nhân nào để cá nhân hoá. count null → AIService giữ mặc định của nó.
                resp = await _http.PostAsJsonAsync("/api/v1/generate-questions",
                    new { jobCategory, cvText = (string?)null, jdText, count }, ct);
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
