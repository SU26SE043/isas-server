using System.Security.Claims;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.CampaignService.Controllers
{
    /// <summary>
    /// D2 — endpoint hướng ỨNG VIÊN (Candidate): xem lời mời → tham gia → my-campaigns → bắt đầu phỏng vấn.
    /// Tách khỏi CampaignController (hướng Employer) — không đụng route Employer hiện có. Public endpoint
    /// (invitations) dùng magic-link, KHÔNG JWT; còn lại yêu cầu role Candidate.
    /// Lưu ý: chi tiết campaign cho ứng viên đặt ở <c>GET /my-campaigns/{id}</c> (KHÔNG dùng lại
    /// <c>GET /campaign/{id}</c> của Employer để tránh trùng route → AmbiguousMatch).
    /// </summary>
    [ApiController]
    public class ParticipationController : ControllerBase
    {
        private readonly IParticipationService _participation;
        private readonly ILogger<ParticipationController> _logger;

        public ParticipationController(IParticipationService participation, ILogger<ParticipationController> logger)
        {
            _participation = participation;
            _logger = logger;
        }

        private Guid? GetCandidateId()
            => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var g)
                ? g : (Guid?)null;

        // ── GET /invitations/{token} — metadata công khai (KHÔNG side-effect) ──────────
        [HttpGet("invitations/{token}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetInvitation(string token, CancellationToken ct)
        {
            try
            {
                var meta = await _participation.GetInvitationMetadataAsync(token, ct);
                return Ok(meta);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
            catch (InvitationGoneException ex) { return StatusCode(StatusCodes.Status410Gone, new { error = ex.Message }); }
        }

        // ── POST /invitations/{token}/join — tham gia campaign ─────────────────────────
        [HttpPost("invitations/{token}/join")]
        [AllowAnonymous]
        public async Task<IActionResult> JoinCampaign(string token, CancellationToken ct)
        {
            try
            {
                var result = await _participation.JoinCampaignAsync(token, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
            catch (InvitationGoneException ex) { return StatusCode(StatusCodes.Status410Gone, new { error = ex.Message }); }
            catch (DownstreamServiceException ex)
            {
                _logger.LogError(ex, "Provision candidate thất bại khi join campaign.");
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "Dịch vụ định danh tạm thời không phản hồi. Vui lòng thử lại sau." });
            }
        }

        // ── GET /my-campaigns — campaign đã join của candidate ─────────────────────────
        [HttpGet("my-campaigns")]
        [Authorize(Roles = "Candidate")]
        public async Task<IActionResult> GetMyCampaigns(CancellationToken ct)
        {
            var candidateId = GetCandidateId();
            if (candidateId is null) return Unauthorized();

            var list = await _participation.GetMyCampaignsAsync(candidateId.Value, ct);
            return Ok(list);
        }

        // ── GET /my-campaigns/{id} — chi tiết campaign cho ứng viên đã join ────────────
        [HttpGet("my-campaigns/{id:guid}")]
        [Authorize(Roles = "Candidate")]
        public async Task<IActionResult> GetMyCampaign(Guid id, CancellationToken ct)
        {
            var candidateId = GetCandidateId();
            if (candidateId is null) return Unauthorized();

            try
            {
                var detail = await _participation.GetCandidateCampaignAsync(candidateId.Value, id, ct);
                return Ok(detail);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        }

        // ── POST /campaign/{id}/start — bắt đầu phỏng vấn (create-or-get session) ───────
        [HttpPost("campaign/{id:guid}/start")]
        [Authorize(Roles = "Candidate")]
        public async Task<IActionResult> StartInterview(Guid id, CancellationToken ct)
        {
            var candidateId = GetCandidateId();
            if (candidateId is null) return Unauthorized();

            try
            {
                var result = await _participation.StartInterviewAsync(candidateId.Value, id, ct);
                return Ok(result);
            }
            catch (UnauthorizedAccessException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }   // campaign không cho phỏng vấn / đã hoàn thành → 409
            catch (DownstreamServiceException ex)
            {
                _logger.LogError(ex, "Tạo session phỏng vấn thất bại (campaign {CampaignId}).", id);
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "Dịch vụ phỏng vấn tạm thời không phản hồi. Vui lòng thử lại sau." });
            }
        }
    }
}
