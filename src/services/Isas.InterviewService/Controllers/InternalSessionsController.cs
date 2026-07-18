using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

/// <summary>
/// D2 — endpoint INTERNAL máy-máy (GEN-1: KHÔNG qua gateway; bảo vệ bằng X-Internal-Token, KHÔNG JWT).
/// CampaignService gọi khi ứng viên bấm "Start Interview" → create-or-get session B2B (I1) idempotent
/// theo (candidateId, campaignId). Câu hỏi + tiêu chí do Campaign cấp sẵn (không gọi AI sinh).
/// </summary>
[ApiController]
public class InternalSessionsController : ControllerBase
{
    private readonly IPracticeService _practiceService;
    private readonly IConfiguration _config;
    private readonly ILogger<InternalSessionsController> _logger;

    public InternalSessionsController(
        IPracticeService practiceService, IConfiguration config, ILogger<InternalSessionsController> logger)
    {
        _practiceService = practiceService;
        _config = config;
        _logger = logger;
    }

    [HttpPost("internal/sessions/campaign")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateOrGetCampaignSession(
        [FromBody] CreateCampaignSessionInternalRequest req,
        [FromHeader(Name = "X-Internal-Token")] string? token,
        CancellationToken ct)
    {
        if (!IsValidInternalToken(token))
            return Unauthorized(new { error = "Invalid internal token" });

        if (req is null || req.Questions is null || req.Questions.Count == 0)
            return BadRequest(new { error = "Campaign session cần ít nhất 1 câu hỏi" });
        if (req.Criteria is null || req.Criteria.Count == 0)
            return BadRequest(new { error = "Campaign session cần ít nhất 1 tiêu chí" });

        // jobCategory string → JobCategory (mềm, ref lỏng xuyên service); không parse được → mặc định BE.
        var jobCategory = Enum.TryParse<JobCategory>(req.JobCategory, ignoreCase: true, out var cat)
            ? cat
            : JobCategory.BE;

        var request = new CreateCampaignSessionRequest(
            req.CampaignId, req.OrgId, jobCategory, req.Questions, req.Criteria, req.ExpiresAt,
            req.AdaptiveEnabled, req.MaxFollowUps, req.MaxQuestions);   // phỏng vấn THÍCH ỨNG (B2B)

        try
        {
            var result = await _practiceService.GetOrCreateCampaignSessionAsync(req.CandidateId, request, ct);
            return Ok(result);
        }
        catch (InsufficientCreditException ex)
        {
            // BK14: ví org hết credit → reserve 402 → KHÔNG tạo session (PAY-5). Campaign map tiếp thành 402.
            _logger.LogWarning(ex, "create-or-get campaign session: ví org hết credit (campaign {CampaignId}, org {OrgId})",
                req.CampaignId, req.OrgId);
            return StatusCode(StatusCodes.Status402PaymentRequired, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "create-or-get campaign session lỗi input (candidate {CandidateId}, campaign {CampaignId})",
                req.CandidateId, req.CampaignId);
            return BadRequest(new { error = ex.Message });
        }
    }

    // DB18 — Payment gọi (máy-máy, X-Internal-Token) để phát hiện orphan reservation: gửi danh sách
    // session_id đang giữ chỗ (Reserved) → trả về TẬP CON thực sự có row practice_sessions. Payment coi
    // phần còn lại là orphan (crash giữa reserve↔insert lúc Start) → release. Input null/rỗng → trả rỗng.
    [HttpPost("internal/sessions/exists")]
    [AllowAnonymous]
    public async Task<IActionResult> SessionsExist(
        [FromBody] SessionExistsRequest req,
        [FromHeader(Name = "X-Internal-Token")] string? token,
        CancellationToken ct)
    {
        if (!IsValidInternalToken(token))
            return Unauthorized(new { error = "Invalid internal token" });

        if (req?.SessionIds is null || req.SessionIds.Count == 0)
            return Ok(new SessionExistsResponse(Array.Empty<Guid>()));

        var existing = await _practiceService.GetExistingSessionIdsAsync(req.SessionIds, ct);
        return Ok(new SessionExistsResponse(existing));
    }

    // AI4 — CampaignService (HR) gọi (máy-máy, X-Internal-Token, KHÔNG qua gateway, KHÔNG JWT) để đọc
    // transcript + nhận xét AI per-criterion + cờ needs_review của 1 buổi B2B → surface cho HR bên bảng
    // kết quả. KHÔNG check chủ session (Campaign đã gate org sở hữu campaign + ranking row thuộc campaign).
    // Session không tồn tại → 404. Trả per-question list (QuestionResponse) kèm answer đầy đủ (transcript/
    // Scores/reasoning/needsReview) — cùng shape con của PracticeSessionResponse.
    [HttpGet("internal/sessions/{sessionId:guid}/answers")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSessionAnswers(
        Guid sessionId,
        [FromHeader(Name = "X-Internal-Token")] string? token,
        CancellationToken ct)
    {
        if (!IsValidInternalToken(token))
            return Unauthorized(new { error = "Invalid internal token" });

        var answers = await _practiceService.GetSessionAnswersInternalAsync(sessionId, ct);
        if (answers is null)
            return NotFound(new { error = $"Session {sessionId} không tồn tại" });

        return Ok(answers);
    }

    private bool IsValidInternalToken(string? token)
    {
        var expected = _config["Internal:Token"];
        if (string.IsNullOrEmpty(expected) || token != expected)
        {
            _logger.LogWarning("create-or-get campaign session bị từ chối: X-Internal-Token sai.");
            return false;
        }
        return true;
    }
}
