using System.Net.Http.Json;
using System.Text.Json;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// D2 — typed HttpClient gọi AuthService POST /internal/auth/provision-candidate (máy-máy,
    /// X-Internal-Token gắn trong client, KHÔNG qua gateway). Lỗi hạ tầng → DownstreamServiceException (502).
    /// Nhái mẫu Interview CreditReservationClient / Campaign AiServiceCriteriaSuggester.
    /// </summary>
    public class AuthProvisionClient : IAuthProvisionClient
    {
        private readonly HttpClient _http;
        private readonly string? _internalToken;
        private readonly ILogger<AuthProvisionClient> _logger;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public AuthProvisionClient(HttpClient http, IConfiguration config, ILogger<AuthProvisionClient> logger)
        {
            _http = http;
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        private record ProvisionApiResponse(Guid CandidateId, string AccessToken);

        public async Task<ProvisionedCandidate> ProvisionCandidateAsync(
            string email, string? fullName, CancellationToken ct = default)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/auth/provision-candidate")
            {
                Content = JsonContent.Create(new { email, fullName })
            };
            msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(msg, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogError(ex, "Không gọi được AuthService /internal/auth/provision-candidate");
                throw new DownstreamServiceException("Không gọi được AuthService (provision candidate)", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("AuthService provision lỗi: {StatusCode} - {Error}", response.StatusCode, error);
                throw new DownstreamServiceException($"AuthService provision trả {(int)response.StatusCode}");
            }

            ProvisionApiResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<ProvisionApiResponse>(Json, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "AuthService provision trả JSON không hợp lệ");
                throw new DownstreamServiceException("AuthService provision trả JSON không hợp lệ", ex);
            }

            if (body is null || string.IsNullOrWhiteSpace(body.AccessToken))
                throw new DownstreamServiceException("AuthService provision trả rỗng");

            return new ProvisionedCandidate(body.CandidateId, body.AccessToken);
        }
    }
}
