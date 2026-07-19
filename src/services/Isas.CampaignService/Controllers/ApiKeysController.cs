using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Isas.CampaignService.Controllers
{
    /// <summary>
    /// F17 — quản lý API key bên thứ ba của ORG (tạo · liệt kê · thu hồi). Xác thực bằng **JWT**
    /// (đây là màn hình quản trị của org, không phải đường bên thứ ba gọi).
    ///
    /// **Chỉ OrgAdmin** (HrMember → 403). Lý do: cấp key = phát một credential đứng lâu, dùng ngoài
    /// phiên, đọc được kết quả + PII ứng viên của TOÀN org, sống lâu hơn nhiệm kỳ người tạo. Đó là
    /// hành vi "uỷ quyền truy cập dữ liệu org", cùng hạng với quản thành viên (AUTH-4 → OrgAdmin),
    /// chứ không phải "quản campaign" (HrMember). AUTH-6 chặn HrMember ở money-mutation vì tiền là
    /// thứ không lấy lại được; dữ liệu ứng viên đã rò cũng vậy — revoke key không thu hồi được bản
    /// sao bên kia đã tải. Liệt kê cũng gate OrgAdmin: danh sách tên+prefix key là bản đồ tích hợp
    /// của org, không phải thông tin vận hành hằng ngày của HR.
    /// </summary>
    [ApiController]
    [Route("campaign/api-keys")]
    [Authorize(Roles = "Employer")]   // A5 — siết tiếp OrgAdmin bằng IsOrgAdmin() trong từng action.
    public class ApiKeysController : ControllerBase
    {
        private readonly IApiKeyService _apiKeys;
        private readonly ILogger<ApiKeysController> _logger;

        public ApiKeysController(IApiKeyService apiKeys, ILogger<ApiKeysController> logger)
        {
            _apiKeys = apiKeys;
            _logger = logger;
        }

        // AUTH-5: JWT mang org_id + org_role khi user thuộc org (đọc OFFLINE — GEN-3).
        private Guid? GetOrgId()
            => Guid.TryParse(User.FindFirstValue("org_id"), out var g) ? g : (Guid?)null;

        private bool IsOrgAdmin()
            => User.HasClaim(c => c.Type == "org_role" && c.Value == "OrgAdmin");

        private Guid GetActorUserId()
            => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : Guid.Empty;

        /// <summary>POST /campaign/api-keys — tạo key. Key thô trả về ĐÚNG MỘT LẦN trong response này.</summary>
        [HttpPost]
        public async Task<ActionResult<CreatedApiKeyResponse>> CreateApiKey(
            [FromBody] CreateApiKeyRequest request, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null || !IsOrgAdmin()) return Forbid();

            try
            {
                var created = await _apiKeys.CreateAsync(orgId.Value, GetActorUserId(), request, ct);
                _logger.LogInformation(
                    "Org {OrgId} tạo API key {KeyId} (pii={Pii}).", orgId, created.Id, created.IncludePii);
                return StatusCode(StatusCodes.Status201Created, created);
            }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to create API key: {ex.Message}"); }
        }

        /// <summary>GET /campaign/api-keys — liệt kê key của org (không kèm key thô).</summary>
        [HttpGet]
        public async Task<ActionResult<List<ApiKeyResponse>>> ListApiKeys(CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null || !IsOrgAdmin()) return Forbid();

            var keys = await _apiKeys.ListAsync(orgId.Value, ct);
            return Ok(keys);
        }

        /// <summary>DELETE /campaign/api-keys/{id} — thu hồi (soft, giữ row cho audit). Idempotent.</summary>
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> RevokeApiKey(Guid id, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null || !IsOrgAdmin()) return Forbid();

            try
            {
                await _apiKeys.RevokeAsync(orgId.Value, GetActorUserId(), id, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to revoke API key: {ex.Message}"); }
        }
    }
}
