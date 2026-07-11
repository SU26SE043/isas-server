using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Isas.CampaignService.Controllers
{
    [ApiController]
    [Route("campaign")]
    [Authorize]
    public class CampaignController : Controller
    {
        private readonly ICampaignService _campaignService;

        public CampaignController(ICampaignService campaignService)
        {
            _campaignService = campaignService;
        }

        [HttpGet]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<List<CampaignResponse>>> GetAllCampaign(CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            return await _campaignService.GetCampaignsAsync(Guid.Parse(employerId), ct);
        }

        [HttpGet("{id}")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> GetCampaignById(Guid id, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                var campaign = await _campaignService.GetCampaignAsync(Guid.Parse(employerId), id, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to get campaign: {ex.Message}"); }
        }

        [HttpPost]
        [Authorize(Roles = "Employer")]
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
        [Authorize(Roles = "Employer")]
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
        [Authorize(Roles = "Employer")]
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
                var fileStream = await _campaignService.DownloadCampaignFilesAsync(Guid.Parse(employerId), id, fileType, ct);

                // 1 file PDF → trả đúng content-type + tên thật (bug #4)
                return File(fileStream, "application/pdf", $"campaign_{id}_{fileType.ToLower()}.pdf");
            }
            catch (KeyNotFoundException) { return NotFound($"Campaign {id} not found."); }
            catch (FileNotFoundException ex) { return NotFound(ex.Message); }   // file chưa upload → 404, không 500 (bug #4)
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to download files: {ex.Message}"); }
        }

        [HttpPut("{id}")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> UpdateCampaign(Guid id, UpdateCampaignRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                // ownership được enforce trong service (lọc theo employerId) → không thấy = 404
                var updatedCampaign = await _campaignService.UpdateCampaignAsync(Guid.Parse(employerId), id, request, ct);
                return Ok(updatedCampaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }         // C12: criteria không hợp lệ → 400
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // C12: sửa criteria khi != Draft → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to update campaign: {ex.Message}"); }
        }

        [HttpPut("{id:guid}/files")]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "Employer")]
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
                var campaign = await _campaignService.UpdateCampaignFilesAsync(Guid.Parse(employerId), id, request, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // C7: sửa khi không Draft → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to update campaign files: {ex.Message}"); }
        }

        [HttpPut("{id:guid}/questions")]
        [Authorize(Roles = "Employer")]
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
                var campaign = await _campaignService.UpdateCampaignQuestionsAsync(Guid.Parse(employerId), id, questions, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // C7: sửa khi không Draft → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to update campaign questions: {ex.Message}"); }
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Employer")]
        public async Task<IActionResult> DeleteCampaign(Guid id, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                // ownership enforce trong service → không thấy = 404
                await _campaignService.DeleteCampaignAsync(Guid.Parse(employerId), id, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to delete campaign: {ex.Message}"); }
        }

        // C8: publish Draft → Active + sinh tiêu chí có cấu trúc
        [HttpPost("{id:guid}/publish")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> PublishCampaign(Guid id, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                var campaign = await _campaignService.PublishCampaignAsync(Guid.Parse(employerId), id, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // sai trạng thái / thiếu câu hỏi → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to publish campaign: {ex.Message}"); }
        }

        // C7: transition Active→Closed→Archived (Draft→Active dùng /publish)
        [HttpPut("{id:guid}/status")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> TransitionStatus(Guid id, [FromBody] TransitionStatusRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                var campaign = await _campaignService.TransitionStatusAsync(Guid.Parse(employerId), id, request.Status, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // transition không hợp lệ → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to transition campaign: {ex.Message}"); }
        }

        // D1: Distribution đường 1 — mời thẳng qua danh sách email
        [HttpPost("{id:guid}/invitations")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CreateInvitationsResponse>> CreateInvitations(Guid id, [FromBody] CreateInvitationsRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            if (request?.Emails == null || request.Emails.Count == 0)
                return BadRequest("At least one email is required.");

            try
            {
                var result = await _campaignService.CreateInvitationsAsync(Guid.Parse(employerId), id, request.Emails, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }          // vượt cap max_candidates
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }    // campaign không Active → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to create invitations: {ex.Message}"); }
        }

        // E5: bảng kết quả — xếp hạng + pass/fail (đọc read-model campaign_rankings, E4).
        // Chỉ chủ org (employer_id) xem được → không phải chủ = 404.
        [HttpGet("{id:guid}/results")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResultsResponse>> GetCampaignResults(Guid id, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                var results = await _campaignService.GetCampaignResultsAsync(Guid.Parse(employerId), id, ct);
                return Ok(results);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to get results: {ex.Message}"); }
        }
    }
}
