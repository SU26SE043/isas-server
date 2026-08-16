using System.Net.Http.Json;
using Isas.CampaignService.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Gọi AIService <c>POST /api/v1/suggest-job-needs</c> (đồng bộ, lúc publish campaign).
    /// Lỗi/timeout → trả null để caller giữ nguyên bộ nhu cầu đang có.
    /// </summary>
    public class AiServiceJobNeedsSuggester : IJobNeedsSuggester
    {
        private readonly HttpClient _http;
        private readonly string? _internalToken;
        private readonly ILogger<AiServiceJobNeedsSuggester> _logger;

        public AiServiceJobNeedsSuggester(
            HttpClient http, IConfiguration config, ILogger<AiServiceJobNeedsSuggester> logger)
        {
            _http = http;
            // GEN-7: endpoint AIService gate X-Internal-Token (fail-closed). Thiếu token = 401 =
            // không có nhu cầu nào ⇒ sàng CV đứng im — hỏng CÂM, nên phải cấu hình Internal:Token.
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        public async Task<List<SuggestedJobNeed>?> SuggestAsync(
            string jdText, string? jobCategory, string language, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(jdText))
                return null;

            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/suggest-job-needs")
                {
                    Content = JsonContent.Create(new { jdText, jobCategory, language })
                };
                msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);
                var resp = await _http.SendAsync(msg, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("AIService /suggest-job-needs → {Status}", resp.StatusCode);
                    return null;
                }

                var body = await resp.Content.ReadFromJsonAsync<ResponseDto>(cancellationToken: ct);
                return body?.Needs?
                    // Nhóm lạ bị loại tại đây chứ không lưu rồi tính sau: `job_needs` đi thẳng vào
                    // prompt sàng CV và vào màn HR, nên giá trị ngoài tập đã biết là rác câm.
                    .Where(n => !string.IsNullOrWhiteSpace(n.Text) && JobNeedCategories.IsValid(n.Category))
                    .Select(n => new SuggestedJobNeed(n.Category, n.Text.Trim()))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gọi AIService /suggest-job-needs lỗi — giữ nguyên job_needs hiện có.");
                return null;
            }
        }

        private sealed record ResponseDto(List<NeedDto>? Needs);
        private sealed record NeedDto(string Category, string Text);
    }
}
