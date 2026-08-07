using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Isas.CampaignService.Services
{
    /// <summary>Gọi AIService POST /api/v1/suggest-criteria (đồng bộ). Lỗi/timeout → trả null để fallback.</summary>
    public class AiServiceCriteriaSuggester : ICriteriaSuggester
    {
        private readonly HttpClient _http;
        private readonly string? _internalToken;
        private readonly ILogger<AiServiceCriteriaSuggester> _logger;

        public AiServiceCriteriaSuggester(
            HttpClient http, IConfiguration config, ILogger<AiServiceCriteriaSuggester> logger)
        {
            _http = http;
            // GEN-7: /suggest-criteria nay gate X-Internal-Token (fail-closed) → đính token như
            // AiServiceFaceVerifyClient. Thiếu token = 401 = fallback default criteria (không crash),
            // nhưng đó là hỏng câm → phải cấu hình Internal:Token cho CampaignService.
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        public async Task<List<SuggestedCriterion>?> SuggestAsync(string jobCategory, string? jdText, string? criteriaText, int count, CancellationToken ct = default)
            => await SuggestAsync(jobCategory, jdText, criteriaText, count, "vi", ct);

        public async Task<List<SuggestedCriterion>?> SuggestAsync(string jobCategory, string? jdText, string? criteriaText, int count, string language, CancellationToken ct)
        {
            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/suggest-criteria")
                {
                    Content = JsonContent.Create(new { jobCategory, jdText, criteriaText, count, language })
                };
                // X-Internal-Token gắn trong client, KHÔNG qua gateway (mirror AiServiceFaceVerifyClient).
                msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);
                var resp = await _http.SendAsync(msg, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("AIService /suggest-criteria → {Status}", resp.StatusCode);
                    return null;
                }

                var body = await resp.Content.ReadFromJsonAsync<ResponseDto>(cancellationToken: ct);
                return body?.Criteria?
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => new SuggestedCriterion(
                        c.Name, c.Description, (decimal)c.Weight, c.MaxScore <= 0 ? 5 : c.MaxScore))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gọi AIService /suggest-criteria lỗi — fallback default criteria.");
                return null;
            }
        }

        private sealed record ResponseDto(List<CriterionDto>? Criteria);
        private sealed record CriterionDto(string Name, string? Description, double Weight, int MaxScore);
    }
}
