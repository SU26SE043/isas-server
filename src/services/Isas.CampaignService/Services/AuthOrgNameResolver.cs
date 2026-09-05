using System.Net.Http.Json;
using System.Text.Json;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// CMP1-B1 — typed HttpClient gọi AuthService <c>GET /internal/auth/organizations/{orgId}</c>
    /// (máy-máy, X-Internal-Token gắn trong client, KHÔNG qua gateway). Nhái mẫu
    /// <see cref="AuthProvisionClient"/>.
    ///
    /// <para>KHÁC <see cref="AuthProvisionClient"/> ở một điểm cốt lõi: mọi lỗi (hạ tầng, non-2xx,
    /// JSON hỏng, rỗng) đều bị NUỐT và trả <c>null</c>. Đây là đường đọc cho ứng viên ẩn danh — Auth
    /// chết KHÔNG được làm hỏng trang lời mời (chỉ mất phần hiển thị tên công ty).</para>
    /// </summary>
    public class AuthOrgNameResolver : IOrgNameResolver
    {
        private readonly HttpClient _http;
        private readonly string? _internalToken;
        private readonly ILogger<AuthOrgNameResolver> _logger;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public AuthOrgNameResolver(HttpClient http, IConfiguration config, ILogger<AuthOrgNameResolver> logger)
        {
            _http = http;
            _internalToken = config["Internal:Token"];
            _logger = logger;
        }

        private record OrgApiResponse(Guid Id, string? Name);

        public async Task<string?> ResolveOrgNameAsync(Guid orgId, CancellationToken ct = default)
        {
            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Get, $"/internal/auth/organizations/{orgId}");
                msg.Headers.TryAddWithoutValidation("X-Internal-Token", _internalToken);

                using var response = await _http.SendAsync(msg, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Resolve tên org {OrgId} thất bại: AuthService trả {StatusCode} — orgName = null.",
                        orgId, (int)response.StatusCode);
                    return null;
                }

                var body = await response.Content.ReadFromJsonAsync<OrgApiResponse>(Json, ct);
                return string.IsNullOrWhiteSpace(body?.Name) ? null : body!.Name!.Trim();
            }
            catch (Exception ex)
            {
                // Nuốt MỌI lỗi (timeout, DNS, socket, JSON hỏng, hủy) — trang lời mời vẫn phải mở.
                _logger.LogWarning(ex, "Không resolve được tên org {OrgId} — orgName = null.", orgId);
                return null;
            }
        }
    }
}
