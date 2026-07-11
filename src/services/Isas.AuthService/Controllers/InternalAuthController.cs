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

            var result = await _authService.ProvisionCandidateAsync(req.Email, req.FullName, ct);
            return Ok(result);
        }

        private bool IsValidInternalToken(string? token)
        {
            var expected = _config["Internal:Token"];
            if (string.IsNullOrEmpty(expected) || token != expected)
            {
                _logger.LogWarning("provision-candidate bị từ chối: X-Internal-Token sai.");
                return false;
            }
            return true;
        }
    }
}
