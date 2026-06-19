using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Isas.CampaignService.Controllers
{
    [ApiController]
    [Route("api/Campaign")]
    //[Authorize]
    public class CampaignController : Controller
    {
        private readonly ICampaignService _campaignService;

        public CampaignController(ICampaignService campaignService)
        {
            _campaignService = campaignService;
        }

        [HttpGet]
        public async Task<ActionResult<List<CampaignResponse>>> GetAllCampaign(CancellationToken ct)
        {
            return await _campaignService.GetCampaignsAsync(ct);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<CampaignResponse>> GetCampaignById(Guid id, CancellationToken ct)
        {
            var campaign = await _campaignService.GetCampaignAsync(id, ct);
            if (campaign == null)
            {
                return NotFound();
            }
            return campaign;
        }

        [HttpPost]
        //[Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> CreateCampaign([FromForm] CreateCampaignRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            // ── 1. Basic request validation before service ──────
            if (string.IsNullOrWhiteSpace(request.Title))
                return BadRequest("Campaign title is required.");

            if (request.Questions == null || !request.Questions.Any())
                return BadRequest("At least one question is required.");

            if (request.Questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                return BadRequest("All questions must have non-empty text.");

            if (request.JdFile is not null && request.JdFile.Length == 0)
                return BadRequest("JD file is empty.");

            if (request.CriteriaFile is not null && request.CriteriaFile.Length == 0)
                return BadRequest("Criteria file is empty.");

            try
            {
                var campaign = await _campaignService.CreateCampaignAsync(Guid.Parse(employerId), request, ct);
                return Ok(campaign);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to create campaign: {ex.Message}");
            }
        }


        [HttpPut("{id}")]
        public async Task<ActionResult<CampaignResponse>> UpdateCampaign(Guid id, UpdateCampaignRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (employerId == null)
                return Forbid();

            var campaign = await _campaignService.GetCampaignAsync(id, ct);
            if (campaign == null)
            {
                return NotFound();
            }
            try
            {
                var updatedCampaign = await _campaignService.UpdateCampaignAsync(id, request, ct);
                return Ok(updatedCampaign);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to update campaign: {ex.Message}");
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteCampaign(Guid id, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (employerId == null)
                return Forbid();

            var campaign = await _campaignService.GetCampaignAsync(id, ct);
            if (campaign == null)
            {
                return NotFound();
            }

            try
            {
                bool deleted = await _campaignService.DeleteCampaignAsync(id, ct);
                if (deleted)
                {
                    return NoContent();
                }
                else
                {
                    return StatusCode(500, "Failed to delete campaign");
                }

            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to delete campaign: {ex.Message}");
            }
        }
    }
}
