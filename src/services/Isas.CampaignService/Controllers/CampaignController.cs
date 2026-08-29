using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.CampaignService.Validation;
using Isas.Shared.Pagination;
using Isas.Shared.Scoring;
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
        private readonly IRubricPreviewService? _preview;   // CAMP-19: chấm thử thước đo
        private readonly IScoringPolicyService? _policies;  // SCP1: chính sách chấm điểm (HĐ-3)
        private readonly ILogger<CampaignController> _logger;

        public CampaignController(
            ICampaignService campaignService,
            ICvScreeningService screening,
            ILogger<CampaignController> logger,
            IRubricPreviewService? preview = null,
            IScoringPolicyService? policies = null)
        {
            _campaignService = campaignService;
            _screening = screening;
            _logger = logger;
            _preview = preview;
            _policies = policies;
        }

        // BK4: chủ sở hữu campaign = ORG (AUTH-8/D5 — billing/campaign gắn theo org). JWT mang `org_id`
        // khi user thuộc org (AUTH-5). Thiếu claim → user không thuộc org nào → không thao tác campaign được.
        private Guid? GetOrgId()
            => Guid.TryParse(User.FindFirstValue("org_id"), out var g) ? g : (Guid?)null;

        // Cá nhân HR thao tác = audit actor (user sub — giữ danh tính người, KHÔNG phải org).
        private Guid GetActorUserId()
            => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : Guid.Empty;

        // GET /campaign — campaign của org caller (mới nhất trước; keyset-paged DB31, mẫu DB8).
        // ?limit= (mặc định/tối đa 500) + ?cursor= (opaque) để phân trang; next-cursor trả ở header
        // X-Next-Cursor (vắng = hết trang). Body giữ nguyên mảng JSON → FE hiện tại không phải sửa gì.
        [HttpGet]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<List<CampaignResponse>>> GetAllCampaign(
            [FromQuery] string? cursor = null, [FromQuery] int? limit = null, CancellationToken ct = default)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            var page = await _campaignService.GetCampaignsAsync(orgId.Value, cursor, limit, ct);
            if (page.NextCursor is not null)
                Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
            return Ok(page.Items);
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

        [HttpGet("{id:guid}/slots")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<IReadOnlyList<CampaignSlotResponse>>> GetSlots(Guid id, CancellationToken ct)
        {
            var orgId = GetOrgId(); if (orgId is null) return Forbid();
            try { return Ok(await _campaignService.GetSlotsAsync(orgId.Value, id, ct)); }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        }

        [HttpPost("{id:guid}/slots")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignSlotResponse>> CreateSlot(Guid id, CreateCampaignSlotRequest request, CancellationToken ct)
        {
            var orgId = GetOrgId(); if (orgId is null) return Forbid();
            try { return Ok(await _campaignService.CreateSlotAsync(orgId.Value, id, request, ct)); }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        }

        [HttpPut("{id:guid}/slots/{slotId:guid}")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignSlotResponse>> UpdateSlot(Guid id, Guid slotId, UpdateCampaignSlotRequest request, CancellationToken ct)
        {
            var orgId = GetOrgId(); if (orgId is null) return Forbid();
            try { return Ok(await _campaignService.UpdateSlotAsync(orgId.Value, id, slotId, request, ct)); }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        }

        [HttpDelete("{id:guid}/slots/{slotId:guid}")]
        [Authorize(Roles = "Employer")]
        public async Task<IActionResult> DeleteSlot(Guid id, Guid slotId, CancellationToken ct)
        {
            var orgId = GetOrgId(); if (orgId is null) return Forbid();
            try { await _campaignService.DeleteSlotAsync(orgId.Value, id, slotId, ct); return NoContent(); }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
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
            catch (EntitlementForbiddenException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ex.Message);
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
            catch (EntitlementForbiddenException ex) { return StatusCode(StatusCodes.Status403Forbidden, ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // C12: sửa criteria khi != Draft → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to update campaign: {ex.Message}"); }
        }

        /// <summary>
        /// HR xem/sửa nhu cầu công việc dùng để sàng CV (replace-all). Chỉ khi campaign `Draft`.
        /// AI đề xuất lúc publish, HR chốt — "AI gợi ý, người quyết" (mẫu D13/SEC-4).
        /// </summary>
        [HttpPut("{id:guid}/job-needs")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> UpdateJobNeeds(
            Guid id, List<JobNeedInput> needs, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var updated = await _campaignService.ReplaceJobNeedsAsync(
                    orgId.Value, GetActorUserId(), id, needs, ct);
                return Ok(updated);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }         // nhóm nhu cầu lạ → 400
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // sửa khi != Draft → 409 (CAMP-2)
            catch (Exception ex) { return StatusCode(500, $"Failed to update job needs: {ex.Message}"); }
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

        // F9 (FR11) — AI sinh câu hỏi từ JD của campaign. Câu sinh ra gắn source=AiGenerated, THAY lượt AI
        // trước đó nhưng GIỮ câu HR tự gõ (bấm nhiều lần không cộng dồn, không nuốt công HR).
        // 400 chưa có JD / JD quá dài / count ngoài 1..20 · 404 ngoài org · 409 không phải Draft (CAMP-2)
        // · 502 AIService lỗi hoặc không sinh được câu nào.
        [HttpPost("{id:guid}/questions/generate")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> GenerateCampaignQuestions(
            Guid id, [FromQuery] int? count, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var campaign = await _campaignService.GenerateCampaignQuestionsAsync(
                    orgId.Value, GetActorUserId(), id, count, ct);
                return Ok(campaign);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            // Lỗi upstream AIService = 502, KHÔNG phải 400 — request của HR hợp lệ, chỉ là AI hỏng
            // (tiền lệ b1239d4). Đặt TRƯỚC ArgumentException/InvalidOperationException để không bị nuốt.
            catch (DownstreamServiceException ex)
            {
                _logger.LogError(ex, "AI sinh câu hỏi thất bại cho campaign {CampaignId}", id);
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // CAMP-2: không Draft → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to generate campaign questions: {ex.Message}"); }
        }

        // Nhập câu hỏi hàng loạt từ file CSV. CHỈ ĐỌC — trả danh sách để HR xem trước; muốn lưu thì HR
        // bấm Lưu và đi qua PUT /questions sẵn có. Không có đường ghi thứ hai, nên guard Draft, audit và
        // merge F10 vẫn nằm đúng một chỗ.
        // 400 file hỏng/sai định dạng/thiếu cột/quá số dòng · 404 ngoài org · 409 không phải Draft (CAMP-2).
        // Lỗi của TỪNG DÒNG trả trong body với mã 200 — một dòng hỏng không huỷ cả file.
        [HttpPost("{id:guid}/questions/import")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(QuestionLimits.ImportMaxFileBytes)]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<ImportQuestionsResult>> ImportQuestions(
            Guid id, [FromForm] IFormFile file, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                return Ok(await _campaignService.ImportQuestionsAsync(orgId.Value, id, file, ct));
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // CAMP-2: không Draft → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to import questions: {ex.Message}"); }
        }

        // CAMP-16 — AI đề xuất MỐC ĐIỂM cho các tiêu chí hiện có. CHỈ ĐỌC: trả về để HR xem/sửa; muốn
        // lưu thì đi qua PUT /campaign/{id} sẵn có (một cửa ghi duy nhất ⇒ validate CAMP-17, audit và
        // luật bump version nằm đúng một chỗ) — cùng nguyên tắc với POST /questions/import.
        // 400 chưa có tiêu chí · 404 ngoài org · 409 chiến dịch đã đóng · 502 AIService lỗi.
        // KHÔNG fallback dải mặc định khi AI hỏng: HR sẽ tin "Mức 3/10" là do AI soạn rồi publish một
        // thước đo chưa ai viết. "Chưa có mốc" vốn là trạng thái hợp lệ nên fail-loud không chặn ai.
        [HttpPost("{id:guid}/criteria/levels/suggest")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<SuggestCriterionLevelsResponse>> SuggestCriterionLevels(
            Guid id, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                return Ok(await _campaignService.SuggestCriterionLevelsAsync(orgId.Value, id, ct));
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            // Đặt TRƯỚC InvalidOperationException: DownstreamServiceException là lỗi upstream (502),
            // request của HR hợp lệ — tiền lệ GenerateCampaignQuestions.
            catch (DownstreamServiceException ex)
            {
                _logger.LogError(ex, "AI soạn mốc điểm thất bại cho campaign {CampaignId}", id);
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to suggest criterion levels: {ex.Message}"); }
        }

        // CAMP-20 — XEM TRƯỚC bộ chuẩn để employer biết mình sắp chép cái gì. CHỈ ĐỌC, không ghi.
        //
        // Employer KHÔNG có cửa nào khác đọc được bộ chuẩn: /internal/rubrics/b2c là máy-máy
        // (X-Internal-Token) còn màn quản trị đòi Roles="Admin". Thiếu endpoint này thì hộp thoại chỉ
        // nói được "sẽ thay thế N tiêu chí" ⇒ employer bấm mù vào đúng thao tác thay cả thước đo.
        //
        // KHÔNG nhận campaignId — bộ chuẩn không thuộc campaign nào. Vẫn gác Roles="Employer" để đây
        // không thành endpoint công khai đọc được toàn bộ thước đo của hệ thống.
        //
        // SCP1 · HĐ-3 — danh sách MẪU chính sách chấm điểm hệ thống (seed). GLOBAL, không org-scoped:
        // mọi Employer thấy cùng bộ mẫu. Route literal 2 đoạn ⇒ KHÔNG đụng [HttpGet("{id}")] (mẫu như
        // "criteria/system-default/preview", "questions/template").
        [HttpGet("scoring-policy-templates")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<IReadOnlyList<DTOs.ScoringPolicyResponse>>> GetScoringPolicyTemplates(
            CancellationToken ct)
        {
            if (_policies is null) return StatusCode(500, "ScoringPolicyService chưa được cấu hình.");
            return Ok(await _policies.GetTemplatesAsync(ct));
        }

        // SCP1 · HĐ-2 — kiểm cú pháp/biến/kết-quả MỘT biểu thức chấm điểm. THUẦN kiểm tra:
        //   · chạy trên BỘ MẪU cố định trong code (ScoringContext.Sample) — không đọc dữ liệu ứng viên,
        //     endpoint dùng được cả khi campaign chưa có ai;
        //   · KHÔNG ghi DB (chỉ 1 lần đọc campaigns để chặn dò campaign org khác → 404).
        // Lỗi biểu thức trả MÃ + [start,end) ký tự (HĐ-2), FE map i18n. `kind` sai/thiếu → 400 (lỗi
        // phong bì request, KHÔNG phải mã lỗi biểu thức).
        [HttpPost("{id:guid}/scoring-policies/validate")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<DTOs.ScoringPolicyValidateResponse>> ValidateScoringPolicy(
            Guid id, [FromBody] DTOs.ScoringPolicyValidateRequest req, CancellationToken ct)
        {
            if (_policies is null) return StatusCode(500, "ScoringPolicyService chưa được cấu hình.");
            var orgId = GetOrgId();
            if (orgId is null) return Forbid();

            var kind = req?.Kind switch
            {
                "Interview" => ScoringExpressionKind.Interview,
                "CvScreening" => ScoringExpressionKind.CvScreening,
                _ => (ScoringExpressionKind?)null,
            };
            if (kind is null) return BadRequest("kind phải là 'Interview' hoặc 'CvScreening'.");

            try
            {
                return Ok(await _policies.ValidateExpressionAsync(
                    orgId.Value, id, kind.Value, req!.Expression ?? string.Empty, ct));
            }
            catch (KeyNotFoundException) { return NotFound(); }
        }

        // ⚠ Route 3 đoạn nên KHÔNG đụng [HttpGet("{id}")] (1 đoạn, không ràng buộc) ở trên.
        //
        // 400 thiếu/sai jobCategory|language · 404 admin CHƯA soạn bộ cho tổ hợp này · 502 Interview lỗi.
        [HttpGet("criteria/system-default/preview")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<SystemDefaultRubricPreviewResponse>> PreviewSystemDefaultCriteria(
            [FromQuery] string? jobCategory, [FromQuery] string? language, CancellationToken ct)
        {
            try
            {
                return Ok(await _campaignService.PreviewSystemDefaultCriteriaAsync(jobCategory, language, ct));
            }
            // 🔴 PHẢI đứng TRƯỚC DownstreamServiceException — nó là lớp DẪN XUẤT. Đảo thứ tự thì khối
            // dưới nuốt mất và "chưa ai soạn bộ này" quay lại thành 502, tức lỗi vận hành đội lốt sự cố.
            catch (SystemRubricNotFoundException ex) { return NotFound(ex.Message); }
            catch (DownstreamServiceException ex)
            {
                _logger.LogError(ex, "Đọc bộ chuẩn để xem trước thất bại ({JobCategory}/{Language})",
                    jobCategory, language);
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to preview system default criteria: {ex.Message}"); }
        }

        // CAMP-20 — chép BỘ CHUẨN B2C (admin soạn) vào campaign. Khác POST .../levels/suggest ở chỗ
        // endpoint này GHI: nó thay thế toàn bộ tiêu chí (kèm mốc) và bump rubric_version khi Active.
        // Ghi thẳng chứ không trả về cho HR bấm Lưu vì đây là thao tác "lấy nguyên bộ có sẵn" — bắt đi
        // vòng qua PUT nghĩa là FE phải tự dựng lại payload criteria[], tức có cơ hội làm rơi mốc.
        // Đường ghi vẫn dùng lại BuildStructuredCriteria + ApplyRubricVersionBump nên validate CAMP-17,
        // chuẩn hoá Σweight và luật bump chỉ có MỘT bản.
        // 400 thiếu/sai jobCategory|language · 404 ngoài org · 409 chiến dịch đã đóng · 502 Interview
        // lỗi hoặc admin chưa soạn bộ chuẩn cho tổ hợp đó.
        [HttpPost("{id:guid}/criteria/from-system-default")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<CampaignResponse>> ApplySystemDefaultCriteria(
            Guid id, [FromBody] ApplySystemDefaultCriteriaRequest? request, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                return Ok(await _campaignService.ApplySystemDefaultCriteriaAsync(
                    orgId.Value, GetActorUserId(), id,
                    request ?? new ApplySystemDefaultCriteriaRequest(), ct));
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            // Đặt TRƯỚC InvalidOperationException: lỗi upstream (502), request của HR hợp lệ —
            // tiền lệ SuggestCriterionLevels/GenerateCampaignQuestions.
            catch (DownstreamServiceException ex)
            {
                _logger.LogError(ex, "Chép bộ chuẩn thất bại cho campaign {CampaignId}", id);
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to apply system default criteria: {ex.Message}"); }
        }

        // CAMP-19 — CHẤM THỬ: AI viết 3 bài mẫu cho một câu hỏi rồi chấm chính chúng bằng thước đo
        // ĐANG LƯU trong DB (không phải bản HR đang gõ dở) ⇒ FE phải khoá nút khi form còn dirty.
        // 3 lượt THÀNH CÔNG đầu của mỗi phiên bản thước đo là miễn phí, sau đó trừ 1 credit ví Org.
        // 400 chưa có tiêu chí/mốc/câu hỏi · 402 org hết credit · 404 ngoài org · 409 chiến dịch đã
        // đóng hoặc đang có lượt chạy · 502 AIService lỗi.
        [HttpPost("{id:guid}/rubric-preview")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<RubricPreviewRunResponse>> RunRubricPreview(
            Guid id, [FromBody] RubricPreviewRequest? request, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();
            if (_preview is null)
                return StatusCode(500, "Rubric preview service chưa được cấu hình.");

            try
            {
                return Ok(await _preview.RunAsync(
                    orgId.Value, GetActorUserId(), id, request ?? new RubricPreviewRequest(), ct));
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            // Ví org hết credit = 402 (PAY-5), KHÔNG phải 502 — HR nạp thêm là chạy được.
            catch (InsufficientOrgCreditException ex)
            {
                return StatusCode(StatusCodes.Status402PaymentRequired, ex.Message);
            }
            catch (DownstreamServiceException ex)
            {
                _logger.LogError(ex, "Chấm thử thất bại cho campaign {CampaignId}", id);
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to run rubric preview: {ex.Message}"); }
        }

        // CAMP-19 — lịch sử chấm thử (20 lượt gần nhất, mới nhất trước) để HR so TRƯỚC/SAU khi sửa mốc.
        // Badge "cùng/khác thước đo" đọc từ rubricFingerprint + rubricVersion + promptVersion.
        [HttpGet("{id:guid}/rubric-preview")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<List<RubricPreviewRunResponse>>> GetRubricPreviewHistory(
            Guid id, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();
            if (_preview is null)
                return StatusCode(500, "Rubric preview service chưa được cấu hình.");

            try
            {
                return Ok(await _preview.GetHistoryAsync(orgId.Value, id, ct));
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to read rubric preview history: {ex.Message}"); }
        }

        // File CSV mẫu. Không cần {id} — định dạng giống nhau cho mọi chiến dịch. Route không đụng
        // [HttpGet("{id:guid}")] vì ràng buộc :guid không khớp chuỗi "questions".
        [HttpGet("questions/template")]
        [Authorize(Roles = "Employer")]
        public IActionResult DownloadQuestionsTemplate()
            => File(QuestionCsvImporter.BuildTemplate(), "text/csv; charset=utf-8", "mau-cau-hoi.csv");

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
            catch (EntitlementForbiddenException ex) { return StatusCode(StatusCodes.Status403Forbidden, ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }    // campaign không Active → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to create invitations: {ex.Message}"); }
        }

        // Danh sách lời mời đã phát — HR theo dõi "đã mời ai / mail gửi tới đâu / ai đã join" và lấy
        // invitationId để reissue (D4). Lọc `?status=Revoked|Joined|Expired|Sent|Queued` + `?search=`
        // (email) — cả hai lọc ở SQL nên đúng trên toàn bộ tập, không chỉ trong 1 trang.
        // Keyset-paged (DB8): `?cursor=&limit=` opt-in, body vẫn mảng JSON, next-cursor ở header
        // X-Next-Cursor (vắng = hết trang). Chỉ chủ org → ngoài org = 404.
        // KHÔNG trả token (DB23 — DB chỉ giữ hash).
        [HttpGet("{id:guid}/invitations")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<List<InvitationListItem>>> GetInvitations(
            Guid id,
            [FromQuery] string? status,
            [FromQuery] string? search,
            [FromQuery] string? cursor,
            [FromQuery] int? limit,
            CancellationToken ct = default)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var page = await _campaignService.GetInvitationsAsync(orgId.Value, id, status, search, cursor, limit, ct);
                if (page.NextCursor is not null)
                    Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
                return Ok(page.Items);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to get invitations: {ex.Message}"); }
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
            catch (EntitlementForbiddenException ex) { return StatusCode(StatusCodes.Status403Forbidden, ex.Message); }
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

        // Log cờ chống gian lận THEO GIÂY (dòng thời gian, khác `Flags` gộp count trong /results). Org-scoped
        // như /results (KHÔNG đòi ranking row như /transcript) — xem được cả session chưa Scored/bỏ ngang.
        [HttpGet("{id:guid}/results/{sessionId:guid}/flags")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<SessionFlagTimelineResponse>> GetSessionFlagTimeline(
            Guid id, Guid sessionId, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var timeline = await _campaignService.GetSessionFlagTimelineAsync(orgId.Value, id, sessionId, ct);
                return Ok(timeline);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (Exception ex) { return StatusCode(500, $"Failed to get flag timeline: {ex.Message}"); }
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
            catch (EntitlementForbiddenException ex) { return StatusCode(StatusCodes.Status403Forbidden, ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }        // campaign chưa Active → 409
            catch (Exception ex) { return StatusCode(500, $"Failed to screen candidates: {ex.Message}"); }
        }

        // C14: shortlist — danh sách ứng viên sàng CV. `?sort=score` (mặc định) DESC theo overall_match_score;
        // `?sort=name`; lọc `?status=&minScore=&search=&skill=`. Chỉ chủ org (org_id) → ngoài org = 404.
        // Keyset-paged (DB8): `?cursor=&limit=` opt-in, body vẫn mảng JSON, next-cursor ở header X-Next-Cursor.
        // ⚠ `?skill=` lọc SAU phân trang (jsonb, không push SQL portable được) → một trang có thể ngắn hơn
        // `limit` hoặc rỗng mà VẪN còn trang sau: đi theo X-Next-Cursor tới khi header vắng, đừng dừng sớm.
        [HttpGet("{id:guid}/candidates")]
        [Authorize(Roles = "Employer")]
        public async Task<ActionResult<List<CandidateListItem>>> GetCandidates(
            Guid id,
            [FromQuery] string? status,
            [FromQuery] int? minScore,
            [FromQuery] string? skill,
            [FromQuery] string? sort,
            [FromQuery] string? search,
            [FromQuery] string? cursor,
            [FromQuery] int? limit,
            CancellationToken ct = default)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var page = await _screening.GetCandidatesAsync(
                    orgId.Value, id, status, minScore, skill, sort, search, cursor, limit, ct);
                if (page.NextCursor is not null)
                    Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
                return Ok(page.Items);
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

        // BK30: HR đẩy lại sàng CV cho 1 ứng viên (điền full_name/điểm còn thiếu, hoặc retry
        // AnalysisFailed — trước đây KHÔNG có đường nào, phải sửa tay trong DB).
        // Invited/Analyzing/Rejected/Pending → 409; CV không có text → 409; ngoài org → 404.
        [HttpPost("{id:guid}/candidates/{candidateId:guid}/rescreen")]
        [Authorize(Roles = "Employer")]
        public async Task<IActionResult> RescreenCandidate(Guid id, Guid candidateId, CancellationToken ct)
        {
            var orgId = GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                await _screening.RescreenCandidateAsync(orgId.Value, id, candidateId, ct);
                return Accepted();
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }   // trạng thái không cho phép
            catch (Exception ex) { return StatusCode(500, $"Failed to rescreen candidate: {ex.Message}"); }
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
