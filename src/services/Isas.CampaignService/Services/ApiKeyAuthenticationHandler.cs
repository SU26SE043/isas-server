using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// F17 — scheme xác thực bằng **API key** (header <c>X-Api-Key</c>) cho Public API bên thứ ba.
    ///
    /// VÌ SAO là AuthenticationHandler chứ không phải if-check trong controller: đăng ký thành scheme
    /// riêng ("ApiKey") khiến ranh giới "key KHÔNG thay được JWT, JWT KHÔNG thay được key" trở thành
    /// **cấu trúc** chứ không phải kỷ luật —
    ///   · endpoint public khai <c>[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme)]</c>
    ///     ⇒ Bearer JWT không bao giờ xác thực được nó (scheme khác không chạy);
    ///   · endpoint JWT giữ scheme mặc định (Bearer) ⇒ X-Api-Key không mở được chúng.
    /// Không có chỗ nào để "quên một nhánh".
    /// </summary>
    public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly IApiKeyService _apiKeys;

        public ApiKeyAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IApiKeyService apiKeys)
            : base(options, logger, encoder)
        {
            _apiKeys = apiKeys;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var raw))
                return AuthenticateResult.NoResult();   // → 401 qua challenge

            var principal = await _apiKeys.AuthenticateAsync(raw.ToString(), Context.RequestAborted);
            if (principal is null)
                return AuthenticateResult.Fail("API key không hợp lệ.");

            // org_id ở đây là NGUỒN SỰ THẬT DUY NHẤT về phạm vi của request này — lấy từ hàng key
            // trong DB, KHÔNG lấy từ bất cứ thứ gì client gửi (không query param, không header khác).
            var identity = new ClaimsIdentity(ApiKeyDefaults.Scheme);
            identity.AddClaim(new Claim(ApiKeyDefaults.OrgIdClaim, principal.OrgId.ToString()));
            identity.AddClaim(new Claim(ApiKeyDefaults.KeyIdClaim, principal.KeyId.ToString()));
            identity.AddClaim(new Claim(ApiKeyDefaults.IncludePiiClaim, principal.IncludePii ? "true" : "false"));

            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(identity), ApiKeyDefaults.Scheme);
            return AuthenticateResult.Success(ticket);
        }

        /// <summary>401 kèm WWW-Authenticate để client ATS biết cần header nào (không lộ lý do hỏng).</summary>
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = $"{ApiKeyDefaults.Scheme} header=\"{ApiKeyDefaults.HeaderName}\"";
            return Task.CompletedTask;
        }
    }

    public static class ApiKeyDefaults
    {
        public const string Scheme = "ApiKey";
        public const string HeaderName = "X-Api-Key";
        public const string OrgIdClaim = "api_key_org_id";
        public const string KeyIdClaim = "api_key_id";
        public const string IncludePiiClaim = "api_key_include_pii";

        /// <summary>Chính sách rate-limit áp cho Public API (partition theo key id).</summary>
        public const string RateLimitPolicy = "api-key";
    }
}
