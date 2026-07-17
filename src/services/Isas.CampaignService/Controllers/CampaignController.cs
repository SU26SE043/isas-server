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

        // BK4: chủ sở hữu campaign = ORG (AUTH-8/D5 — billing/campaign gắn theo org). JWT mang `org_id`
        // khi user thuộc org (AUTH-5). Thiếu claim → user không thuộc org nào → không thao tác campaign được.
        private Guid? GetOrgId()
            => Guid.TryParse(User.FindFirstValue("org_id"), out var g) ? g : (Guid?)null;

        // Cá nhân HR thao tác = audit actor (user sub — giữ danh tính người, KHÔNG phải org).
        private Guid GetActorUserId()
            => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : Guid.Empty;

        [HttpGet]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<List<CampaignResponse>>> GetAllCampaign(CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            return await _campaignService.GetCampaignsAsync(orgId.Value, ct);
        }

        [HttpGet("{id}")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> GetCampaignById(Guid id, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var campaign = await _campaignService.GetCampaignAsync(orgId.Value, id, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to get campaign: {ex.Message}"); }
        }

        [HttpPost]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> CreateCampaign([FromBody] CreateCampaignRequest request, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
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
                var campaign = await _campaignService.CreateCampaignAsync(orgId.Value, GetActorUserId(), request, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            if (request.JdFile is null && request.CriteriaFile is null)
                return BadRequest("At least one file (JdFile or CriteriaFile) must be provided.");

            if (request.JdFile != null && request.JdFile.Length > 10 * 1024 * 1024)
                return BadRequest("JD file size cannot exceed 10MB.");

            if (request.CriteriaFile != null && request.CriteriaFile.Length > 10 * 1024 * 1024)
                return BadRequest("Criteria file size cannot exceed 10MB.");

            try
            {
                var campaign = await _campaignService.UploadCampaignFilesAsync(orgId.Value, id, request, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            if (string.IsNullOrWhiteSpace(fileType) || !(fileType.Equals("jd", StringComparison.OrdinalIgnoreCase) || fileType.Equals("criteria", StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest("Invalid fileType. Must be 'jd' or 'criteria'.");
            }

            try
            {
                var fileStream = await _campaignService.DownloadCampaignFilesAsync(orgId.Value, id, fileType, ct);

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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                // ownership được enforce trong service (lọc theo org_id) → không thấy = 404
                var updatedCampaign = await _campaignService.UpdateCampaignAsync(orgId.Value, GetActorUserId(), id, request, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            if (request.JdFile is null && request.CriteriaFile is null)
                return BadRequest("At least one file must be provided.");

            if (request.JdFile != null && request.JdFile.Length > 10 * 1024 * 1024)
                return BadRequest("JD file size cannot exceed 10MB.");

            if (request.CriteriaFile != null && request.CriteriaFile.Length > 10 * 1024 * 1024)
                return BadRequest("Criteria file size cannot exceed 10MB.");

            try
            {
                var campaign = await _campaignService.UpdateCampaignFilesAsync(orgId.Value, id, request, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            if (questions == null || !questions.Any())
                return BadRequest("At least one question is required.");

            if (questions.Any(q => string.IsNullOrWhiteSpace(q.QuestionText)))
                return BadRequest("All questions must have non-empty text.");
            try
            {
                var campaign = await _campaignService.UpdateCampaignQuestionsAsync(orgId.Value, GetActorUserId(), id, questions, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                // ownership enforce trong service → không thấy = 404
                await _campaignService.DeleteCampaignAsync(orgId.Value, GetActorUserId(), id, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var campaign = await _campaignService.PublishCampaignAsync(orgId.Value, GetActorUserId(), id, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var campaign = await _campaignService.TransitionStatusAsync(orgId.Value, GetActorUserId(), id, request.Status, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            if (request?.Emails == null || request.Emails.Count == 0)
                return BadRequest("At least one email is required.");

            try
            {
                var result = await _campaignService.CreateInvitationsAsync(orgId.Value, GetActorUserId(), id, request.Emails, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }          // vượt cap max_candidates
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }    // campaign không Active → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to create invitations: {ex.Message}"); }
        }

        // D4: phát lại lời mời — vô hiệu token cũ + phát token mới + resend email.
        // Ngoài org / invitation không thuộc campaign → 404; campaign không Active → 409.
        [HttpPost("{id:guid}/invitations/{invitationId:guid}/reissue")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<InvitationItem>> ReissueInvitation(Guid id, Guid invitationId, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var result = await _campaignService.ReissueInvitationAsync(orgId.Value, GetActorUserId(), id, invitationId, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }    // campaign không Active → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to reissue invitation: {ex.Message}"); }
        }

        // C15: Distribution đường 2 — mời hàng loạt từ shortlist sàng CV (candidateIds → tách email từ CV).
        // Vượt max_candidates → 400; campaign không Active → 409; ngoài org → 404. Per-item lỗi vào failed[].
        [HttpPost("{id:guid}/candidates/invite")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<InviteShortlistResponse>> InviteShortlistedCandidates(
            Guid id, [FromBody] InviteShortlistRequest request, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            if (request?.CandidateIds == null || request.CandidateIds.Count == 0)
                return BadRequest("At least one candidateId is required.");

            try
            {
                var result = await _campaignService.InviteShortlistedCandidatesAsync(orgId.Value, GetActorUserId(), id, request.CandidateIds, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }          // vượt cap max_candidates
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }    // campaign không Active → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to invite shortlisted candidates: {ex.Message}"); }
        }

        // E5: bảng kết quả — xếp hạng + pass/fail (đọc read-model campaign_rankings, E4).
        // Chỉ chủ org (org_id) xem được → không phải chủ = 404.
        [HttpGet("{id:guid}/results")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResultsResponse>> GetCampaignResults(Guid id, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var results = await _campaignService.GetCampaignResultsAsync(orgId.Value, id, ct);
                return Ok(results);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to get results: {ex.Message}"); }
        }

        // E11b: HR chốt/sửa điểm-kết-quả cuối 1 ứng viên (điểm AI = gợi ý — D13). Org-scoped → ngoài org 404.
        // Note bắt buộc (audit); Score=null & Result=null → clear (về AI). Result chỉ 'Pass'/'Fail'.
        [HttpPut("{id:guid}/results/{sessionId:guid}/override")]
        [Authorize(Roles = "Employer")]
        public async Task<IActionResult> OverrideResult(
            Guid id, Guid sessionId, [FromBody] OverrideResultRequest request, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                await _campaignService.OverrideResultAsync(orgId.Value, GetActorUserId(), id, sessionId, request, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return StatusCode(500, $"Failed to override result: {ex.Message}"); }
        }

        // AI4: HR xem chi tiết transcript + nhận xét AI per-criterion + cờ needs_review 1 buổi (đối chiếu điểm
        // ranking). Org-scoped GIỐNG override (org sở hữu campaign + ranking row thuộc campaign) → ngoài org /
        // session chưa chấm = 404. Transcript đọc xuyên-service từ Interview (internal); Interview lỗi → 502.
        [HttpGet("{id:guid}/results/{sessionId:guid}/transcript")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<SessionTranscriptResponse>> GetSessionTranscript(
            Guid id, Guid sessionId, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var detail = await _campaignService.GetSessionTranscriptAsync(orgId.Value, id, sessionId, ct);
                return Ok(detail);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (DownstreamServiceException ex)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
            }
            catch (Exception ex) { return StatusCode(500, $"Failed to get transcript: {ex.Message}"); }
        }

        // E6: xuất bảng kết quả (E5) ra file. `?format=csv` (mặc định khi thiếu); `pdf`/khác → 400.
        // Ownership giống E5 (lọc theo org_id) → ngoài org = 404. Bám pattern `return File(...)`.
        [HttpGet("{id:guid}/results/export")]
        [Authorize(Roles = "Employer")]
        public async Task<IActionResult> ExportCampaignResults(Guid id, [FromQuery] string? format, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var export = await _campaignService.ExportCampaignResultsAsync(orgId.Value, id, format, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            if (files is null || files.Count == 0)
                return BadRequest("At least one CV file (PDF) is required.");

            try
            {
                var result = await _campaignService.ScreenCandidatesAsync(orgId.Value, GetActorUserId(), id, files, ct);

                // C14: đẩy job AI chấm khớp cho các ứng viên vừa Filtered (Filtered → Analyzing). Best-effort:
                // broker down → giữ Filtered (last_screening_published_at=null) → C15 republisher đẩy lại,
                // KHÔNG làm hỏng kết quả sàng đã lưu (202 vẫn trả).
                try { await _screening.PublishScreeningJobsAsync(orgId.Value, id, ct); }
                catch (Exception ex) { _logger.LogError(ex, "Publish job sàng CV thất bại cho campaign {CampaignId}", id); }

                return StatusCode(StatusCodes.Status202Accepted, result);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }             // vượt cap / thiếu file → 400
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }        // campaign chưa Active → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to screen candidates: {ex.Message}"); }
        }

        // C14: shortlist — danh sách ứng viên sàng CV. `?sort=score` (mặc định) DESC theo overall_match_score;
        // `?sort=name`; lọc `?status=&minScore=&skill=`. Chỉ chủ org (org_id) → ngoài org = 404.
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var list = await _screening.GetCandidatesAsync(orgId.Value, id, status, minScore, skill, sort, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var detail = await _screening.GetCandidateAsync(orgId.Value, id, candidateId, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                await _screening.PatchCandidateAsync(orgId.Value, GetActorUserId(), id, candidateId, request, ct);
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
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var stream = await _campaignService.DownloadCandidateCvAsync(orgId.Value, id, candidateId, ct);
                return File(stream, "application/pdf", $"candidate_{candidateId}.pdf");
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (FileNotFoundException ex) { return NotFound(ex.Message); }   // chưa archive → 404
            catch (Exception ex) { return StatusCode(500, $"Failed to download CV: {ex.Message}"); }
        }
    }
}
