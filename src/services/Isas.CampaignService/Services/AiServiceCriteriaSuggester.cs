using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Isas.CampaignService.Services
{
    /// <summary>Gọi AIService POST /api/v1/suggest-criteria (đồng bộ). Lỗi/timeout → trả null để fallback.</summary>
    public class AiServiceCriteriaSuggester : ICriteriaSuggester
    {
        private readonly HttpClient _http;
        private readonly ILogger<AiServiceCriteriaSuggester> _logger;

        public AiServiceCriteriaSuggester(HttpClient http, ILogger<AiServiceCriteriaSuggester> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<List<SuggestedCriterion>?> SuggestAsync(
            string jobCategory, string? jdText, string? criteriaText, int count, CancellationToken ct = default)
        {
            try
            {
                var resp = await _http.PostAsJsonAsync("/api/v1/suggest-criteria",
                    new { jobCategory, jdText, criteriaText, count }, ct);

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
