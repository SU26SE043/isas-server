using System.Runtime.CompilerServices;
using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.AuthService.Controllers
{
    /// <summary>
    /// D2 — endpoint INTERNAL máy-máy (GEN-1: KHÔNG qua gateway; bảo vệ bằng X-Internal-Token, KHÔNG JWT).
    /// CampaignService gọi khi ứng viên tham gia campaign qua magic-link → provision account Candidate nhẹ.
    /// </summary>
    [ApiController]
    [Route("internal/auth")]
    public class InternalAuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IConfiguration _config;
        private readonly ILogger<InternalAuthController> _logger;

        public InternalAuthController(
            IAuthService authService, IConfiguration config, ILogger<InternalAuthController> logger)
        {
            _authService = authService;
            _config = config;
            _logger = logger;
        }

        // Create-or-get Candidate theo email (idempotent) → { candidateId, accessToken }. Token sai → 401.
        [HttpPost("provision-candidate")]
        [AllowAnonymous]
        public async Task<ActionResult<ProvisionCandidateResponse>> ProvisionCandidate(
            [FromBody] ProvisionCandidateRequest req,
            [FromHeader(Name = "X-Internal-Token")] string? token,
            CancellationToken ct)
        {
            if (!IsValidInternalToken(token))
                return Unauthorized(new { error = "Invalid internal token" });

            if (req is null || string.IsNullOrWhiteSpace(req.Email))
                return BadRequest(new { error = "Email is required" });

            try
            {
                var result = await _authService.ProvisionCandidateAsync(req.Email, req.FullName, ct);
                return Ok(result);
            }
            catch (UserBannedException ex)
            {
                // F20 — người đã bị đình chỉ không được vào bài qua magic-link B2B. 403 để
                // CampaignService phân biệt với "token internal sai" (401) và báo đúng cho ứng viên.
                _logger.LogWarning("provision-candidate bị từ chối: account đã bị đình chỉ.");
                return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
            }
        }

        // CMP1-B1 — resolve tên tổ chức theo org_id (máy-máy). CampaignService chỉ giữ org_id (GEN-2:
        // không FK xuyên service) nên trang lời mời phải hỏi Auth để hiển thị tên công ty mời.
        // Không tồn tại → 404 (caller coi như orgName = null). Token sai → 401.
        [HttpGet("organizations/{orgId:guid}")]
        [AllowAnonymous]
        public async Task<ActionResult<InternalOrganizationResponse>> GetOrganization(
            Guid orgId,
            [FromHeader(Name = "X-Internal-Token")] string? token,
            CancellationToken ct)
        {
            if (!IsValidInternalToken(token))
                return Unauthorized(new { error = "Invalid internal token" });

            try
            {
                var org = await _authService.GetOrganizationAsync(orgId, ct);
                return Ok(new InternalOrganizationResponse(org.Id, org.Name));
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }

        // CMP1 fix — dùng chung cho CẢ HAI endpoint (ProvisionCandidate + GetOrganization). Trước đó
        // log hardcode "provision-candidate bị từ chối" nên một token sai ở GetOrganization cũng in
        // ra log của endpoint kia — sai khi chẩn đoán ai đang gọi hỏng. [CallerMemberName] lấy tên
        // action gọi tới KHÔNG cần sửa call-site nào.
        private bool IsValidInternalToken(
            string? token, [CallerMemberName] string caller = "")
        {
            var expected = _config["Internal:Token"];
            if (string.IsNullOrEmpty(expected) || token != expected)
            {
                _logger.LogWarning("{Caller} bị từ chối: X-Internal-Token sai.", caller);
                return false;
            }
            return true;
        }
    }
}
