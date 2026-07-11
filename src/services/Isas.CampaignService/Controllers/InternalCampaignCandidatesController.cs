using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.CampaignService.Controllers
{
    /// <summary>
    /// C14 — Callback INTERNAL từ worker sàng CV (AIService) → Campaign (GEN-1: KHÔNG qua gateway;
    /// bảo vệ bằng X-Internal-Token, KHÔNG JWT). Ghi kết quả AI lên campaign_candidates +
    /// candidate_criterion_scores. Idempotent — worker không cần retry khi no-op.
    /// </summary>
    [ApiController]
    [Route("internal/campaign-candidates")]
    public class InternalCampaignCandidatesController : ControllerBase
    {
        private readonly ICvScreeningService _screening;
        private readonly IConfiguration _config;
        private readonly ILogger<InternalCampaignCandidatesController> _logger;

        public InternalCampaignCandidatesController(
            ICvScreeningService screening,
            IConfiguration config,
            ILogger<InternalCampaignCandidatesController> logger)
        {
            _screening = screening;
            _config = config;
            _logger = logger;
        }

        // Worker chấm khớp xong → lưu điểm + status Analyzed. Idempotent + recover ngoài thứ tự (trừ Invited).
        [HttpPost("{candidateId:guid}/cv-result")]
        [AllowAnonymous]
        public async Task<IActionResult> CvResult(
            Guid candidateId,
            [FromBody] CvResultCallbackRequest req,
            [FromHeader(Name = "X-Internal-Token")] string? token,
            CancellationToken ct)
        {
            if (!IsValidInternalToken(token, candidateId))
                return Unauthorized(new { error = "Invalid internal token" });

            try
            {
                // Mọi outcome (Analyzed / skip-Invited) đều là no-op thành công → 204, worker KHÔNG retry.
                await _screening.SaveCvResultAsync(candidateId, req, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        // Worker báo lỗi vĩnh viễn khi phân tích 1 CV → status AnalysisFailed (không hạ cấp Analyzed/Invited).
        [HttpPost("{candidateId:guid}/cv-failed")]
        [AllowAnonymous]
        public async Task<IActionResult> CvFailed(
            Guid candidateId,
            [FromBody] CvFailedCallbackRequest req,
            [FromHeader(Name = "X-Internal-Token")] string? token,
            CancellationToken ct)
        {
            if (!IsValidInternalToken(token, candidateId))
                return Unauthorized(new { error = "Invalid internal token" });

            try
            {
                await _screening.MarkCvFailedAsync(candidateId, req?.Reason, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        private bool IsValidInternalToken(string? token, Guid candidateId)
        {
            var expected = _config["Internal:Token"];
            if (string.IsNullOrEmpty(expected) || token != expected)
            {
                _logger.LogWarning("Callback sàng CV bị từ chối: token sai cho candidate {CandidateId}", candidateId);
                return false;
            }
            return true;
        }
    }
}
