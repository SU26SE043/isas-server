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
        private readonly ICvScreeningService _screening;   // C14: sàng CV async (publish/shortlist/PATCH)
        private readonly ILogger<CampaignController> _logger;

        public CampaignController(
            ICampaignService campaignService,
            ICvScreeningService screening,
            ILogger<CampaignController> logger)
        {
            _campaignService = campaignService;
            _screening = screening;
            _logger = logger;
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

        // C15: Distribution đường 2 — mời hàng loạt từ shortlist sàng CV (candidateIds → tách email từ CV).
        // Vượt max_candidates → 400; campaign không Active → 409; ngoài org → 404. Per-item lỗi vào failed[].
        [HttpPost("{id:guid}/candidates/invite")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<InviteShortlistResponse>> InviteShortlistedCandidates(
            Guid id, [FromBody] InviteShortlistRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            if (request?.CandidateIds == null || request.CandidateIds.Count == 0)
                return BadRequest("At least one candidateId is required.");

            try
            {
                var result = await _campaignService.InviteShortlistedCandidatesAsync(Guid.Parse(employerId), id, request.CandidateIds, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }          // vượt cap max_candidates
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }    // campaign không Active → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to invite shortlisted candidates: {ex.Message}"); }
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

        // E6: xuất bảng kết quả (E5) ra file. `?format=csv` (mặc định khi thiếu); `pdf`/khác → 400.
        // Ownership giống E5 (lọc theo employer_id) → ngoài org = 404. Bám pattern `return File(...)`.
        [HttpGet("{id:guid}/results/export")]
        [Authorize(Roles = "Employer")]
        public async Task<IActionResult> ExportCampaignResults(Guid id, [FromQuery] string? format, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                var export = await _campaignService.ExportCampaignResultsAsync(Guid.Parse(employerId), id, format, ct);
                return File(export.Content, export.ContentType, export.FileName);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }        // ngoài org / không tồn tại → 404
            catch (ArgumentException ex) { return BadRequest(ex.Message); }         // format không hỗ trợ → 400
            catch (Exception ex) { return StatusCode(500, $"Failed to export results: {ex.Message}"); }
        }

        // C13: sàng CV hàng loạt — upload nhiều PDF → parse + archive + hard-filter (0 credit).
        // Vượt cap/thiếu file → 400; campaign chưa Active → 409; ngoài org → 404.
        [HttpPost("{id:guid}/candidates")]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "Employer")]
        public async Task<IActionResult> ScreenCandidates(Guid id, [FromForm] IFormFileCollection files, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            if (files is null || files.Count == 0)
                return BadRequest("At least one CV file (PDF) is required.");

            try
            {
                var result = await _campaignService.ScreenCandidatesAsync(Guid.Parse(employerId), id, files, ct);

                // C14: đẩy job AI chấm khớp cho các ứng viên vừa Filtered (Filtered → Analyzing). Best-effort:
                // broker down → giữ Filtered (last_screening_published_at=null) → C15 republisher đẩy lại,
                // KHÔNG làm hỏng kết quả sàng đã lưu (202 vẫn trả).
                try { await _screening.PublishScreeningJobsAsync(Guid.Parse(employerId), id, ct); }
                catch (Exception ex) { _logger.LogError(ex, "Publish job sàng CV thất bại cho campaign {CampaignId}", id); }

                return StatusCode(StatusCodes.Status202Accepted, result);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }             // vượt cap / thiếu file → 400
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }        // campaign chưa Active → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to screen candidates: {ex.Message}"); }
        }

        // C14: shortlist — danh sách ứng viên sàng CV. `?sort=score` (mặc định) DESC theo overall_match_score;
        // `?sort=name`; lọc `?status=&minScore=&skill=`. Chỉ chủ org (employer_id) → ngoài org = 404.
        [HttpGet("{id:guid}/candidates")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<List<CandidateListItem>>> GetCandidates(
            Guid id,
            [FromQuery] string? status,
            [FromQuery] int? minScore,
            [FromQuery] string? skill,
            [FromQuery] string? sort,
            CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                var list = await _screening.GetCandidatesAsync(Guid.Parse(employerId), id, status, minScore, skill, sort, ct);
                return Ok(list);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to get candidates: {ex.Message}"); }
        }

        // C14: chi tiết 1 ứng viên (summary, skills, điểm + reasoning từng tiêu chí + KEY CV gốc).
        [HttpGet("{id:guid}/candidates/{candidateId:guid}")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CandidateDetailResponse>> GetCandidate(Guid id, Guid candidateId, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                var detail = await _screening.GetCandidateAsync(Guid.Parse(employerId), id, candidateId, ct);
                return Ok(detail);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to get candidate: {ex.Message}"); }
        }

        // C14: HR bổ sung/sửa email/fullName khi CV không tách được (ghi audit_logs). Đã Invited → 409.
        [HttpPatch("{id:guid}/candidates/{candidateId:guid}")]
        [Authorize(Roles = "Employer")]
        public async Task<IActionResult> PatchCandidate(
            Guid id, Guid candidateId, [FromBody] PatchCandidateRequest request, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                await _screening.PatchCandidateAsync(Guid.Parse(employerId), id, candidateId, request, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }             // email rỗng/sai/trùng → 400
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }        // đã Invited → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to update candidate: {ex.Message}"); }
        }

        // C13: serve CV gốc (PDF) cho HR. cv_file_url null → 404; ngoài org → 404.
        [HttpGet("{id:guid}/candidates/{candidateId:guid}/cv")]
        [Authorize(Roles = "Employer")]
        public async Task<IActionResult> DownloadCandidateCv(Guid id, Guid candidateId, CancellationToken ct)
        {
            var employerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(employerId))
                return Forbid();

            try
            {
                var stream = await _campaignService.DownloadCandidateCvAsync(Guid.Parse(employerId), id, candidateId, ct);
                return File(stream, "application/pdf", $"candidate_{candidateId}.pdf");
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (FileNotFoundException ex) { return NotFound(ex.Message); }   // chưa archive → 404
            catch (Exception ex) { return StatusCode(500, $"Failed to download CV: {ex.Message}"); }
        }
    }
}
