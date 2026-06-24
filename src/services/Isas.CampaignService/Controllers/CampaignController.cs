using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Isas.CampaignService.Controllers
{
    [ApiController]
    [Route("campaign")]
    //[Authorize]
    public class CampaignController : Controller
    {
        private readonly ICampaignService _campaignService;

        public CampaignController(ICampaignService campaignService)
        {
            _campaignService = campaignService;
        }

        [HttpGet]
        //[Authorize(Roles = "Employer")]
        public async Task<ActionResult<List<CampaignResponse>>> GetAllCampaign(CancellationToken ct)
        {
            return await _campaignService.GetCampaignsAsync(ct);
        }

        [HttpGet("{id}")]
        //[Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> GetCampaignById(Guid id, CancellationToken ct)
        {
            try
            {
                var campaign = await _campaignService.GetCampaignAsync(id, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to get campaign: {ex.Message}"); }
        }

        [HttpPost]
        //[Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> CreateCampaign([FromBody] CreateCampaignRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            if (request.Questions == null || !request.Questions.Any())
                return BadRequest("At least one question is required.");

            if (request.Questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                return BadRequest("All questions must have non-empty text.");

            if (request.StartsAt.HasValue && request.StartsAt < DateTime.UtcNow)
                return BadRequest("StartsAt cannot be in the past.");

            if (request.ExpiresAt.HasValue && request.ExpiresAt < DateTime.UtcNow)
                return BadRequest("ExpiresAt cannot be in the past.");

            if (request.StartsAt.HasValue && request.ExpiresAt.HasValue && request.StartsAt >= request.ExpiresAt)
                return BadRequest("StartsAt must be before ExpiresAt.");

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

        [HttpPost("{id:guid}/files")]
        [Consumes("multipart/form-data")]
        //[Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> UploadCampaignFiles(Guid id, [FromForm] UploadCampaignFilesRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            if (request.JdFile is null && request.CriteriaFile is null)
                return BadRequest("At least one file (JdFile or CriteriaFile) must be provided.");

            if (request.JdFile != null && request.JdFile.Length > 10 * 1024 * 1024)
                return BadRequest("JD file size cannot exceed 10MB.");

            if (request.CriteriaFile != null && request.CriteriaFile.Length > 10 * 1024 * 1024)
                return BadRequest("Criteria file size cannot exceed 10MB.");

            try
            {
                var campaign = await _campaignService.UploadCampaignFilesAsync(Guid.Parse(employerId), id, request, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException) { return NotFound($"Campaign {id} not found."); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to upload files: {ex.Message}"); }
        }

        [HttpPost("{id:guid}/files/download")]
        //[Authorize(Roles = "Employer")]
        public async Task<IActionResult> DownloadCampaignFiles(Guid id, string fileType, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            if (string.IsNullOrWhiteSpace(fileType) || !(fileType.Equals("jd", StringComparison.OrdinalIgnoreCase) || fileType.Equals("criteria", StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest("Invalid fileType. Must be 'jd' or 'criteria'.");
            }

            try
            {
                var fileStream = await _campaignService.DownloadCampaignFilesAsync(id, fileType, ct);

                if (fileStream == null)
                {
                    return NotFound($"No files found for campaign {id}.");
                }

                return File(fileStream, "application/zip", $"Campaign_{id}_Files.zip");
            }
            catch (KeyNotFoundException) { return NotFound($"Campaign {id} not found."); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to download files: {ex.Message}"); }
        }

        [HttpPut("{id}")]
        //[Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> UpdateCampaign(Guid id, UpdateCampaignRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            var campaign = await _campaignService.GetCampaignAsync(id, ct);
            if (campaign == null)
            {
                return NotFound();
            }

            if(campaign.EmployerId != Guid.Parse(employerId))
            {
                return Forbid();
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

        [HttpPut("{id:guid}/files")]
        [Consumes("multipart/form-data")]
        //[Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> UpdateCampaignFiles(Guid id, [FromForm] UploadCampaignFilesRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            if (request.JdFile is null && request.CriteriaFile is null)
                return BadRequest("At least one file must be provided.");

            if (request.JdFile != null && request.JdFile.Length > 10 * 1024 * 1024)
                return BadRequest("JD file size cannot exceed 10MB.");

            if (request.CriteriaFile != null && request.CriteriaFile.Length > 10 * 1024 * 1024)
                return BadRequest("Criteria file size cannot exceed 10MB.");

            try
            {
                var campaign = await _campaignService.UpdateCampaignFilesAsync(id, request, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to update campaign files: {ex.Message}"); }
        }

        [HttpPut("{id:guid}/questions")]
        //[Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> UpdateCampaignQuestions(Guid id, [FromBody] List<QuestionItem> questions, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            if (questions == null || !questions.Any())
                return BadRequest("At least one question is required.");

            if (questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                return BadRequest("All questions must have non-empty text.");
            try
            {
                var campaign = await _campaignService.UpdateCampaignQuestionsAsync(id, questions, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to update campaign questions: {ex.Message}"); }
        }

        [HttpDelete("{id}")]
        //[Authorize(Roles = "Employer")]
        public async Task<IActionResult> DeleteCampaign(Guid id, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
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
