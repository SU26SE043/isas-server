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

        // QuestionDetails (câu + đáp án mẫu) chỉ dùng khi ĐỦ và KHỚP SỐ LƯỢNG với Questions. Lệch số
        // lượng nghĩa là hai phía đang nói về hai bộ câu khác nhau — ghép theo chỉ số lúc đó sẽ gán đáp
        // án của câu này cho câu kia, tức chấm sai mà không lỗi nào nổ. Thà bỏ đáp án (chấm như trước)
        // còn hơn chấm bằng đáp án gán nhầm.
        var details = req.QuestionDetails is { Count: > 0 } d && d.Count == req.Questions.Count
            ? d
            : null;
        if (req.QuestionDetails is { Count: > 0 } mismatched && mismatched.Count != req.Questions.Count)
            _logger.LogWarning(
                "create-or-get campaign session {CampaignId}: questionDetails có {Details} phần tử nhưng "
                + "questions có {Questions} — bỏ qua đáp án mẫu cho buổi này",
                req.CampaignId, mismatched.Count, req.Questions.Count);

        var criteria = SanitizeCriterionLevels(req.Criteria, req.CampaignId);

        var request = new CreateCampaignSessionRequest(
            req.CampaignId, req.OrgId, jobCategory, req.Questions, criteria, req.ExpiresAt,
            req.AdaptiveEnabled, req.MaxFollowUps, req.MaxQuestions,
            req.MaxDeepPerQuestion, req.Language, req.Seniority,   // phỏng vấn THÍCH ỨNG (B2B) — INT-17b: trần đào sâu mỗi câu
            req.RubricVersion,
            details,
            // SCP1 · B5 — chuyển tiếp hợp đồng chấm điểm (chính sách biểu thức) từ Campaign xuống session.
            req.CampaignPolicyVersion, req.CampaignPolicyExpression,
            req.CampaignPolicyPassScorePct, req.CampaignPolicyEngineVersion,
            // RNK1 · HĐ-2 / CAMP-21 — chuyển tiếp luật câu bỏ trống.
            req.SkipPenalty);

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
        catch (CapacityExceededException ex)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = ex.Message, code = "platform_capacity_exceeded", retryAfterSeconds = 60
            });
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
    //
    // R1 — trả THÊM `states` (session_id + status string) để Payment phân nhánh chỗ giữ của session ĐÃ
    // TERMINAL (Scored → consume · SessionAbandoned/Failed → release), thứ trước R1 không ai dọn.
    // Mở rộng ADDITIVE: `existingIds` giữ nguyên nghĩa + vị trí ⇒ Payment bản cũ không vỡ.
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
            return Ok(new SessionExistsResponse(Array.Empty<Guid>(), Array.Empty<SessionStateDto>()));

        // MỘT nguồn cho cả 2 trường: `existingIds` suy ra từ `states` ⇒ hai mảng không thể lệch tập
        // (lệch tập = Payment thấy session "tồn tại mà thiếu status" → SKIP oan chỗ giữ cần dọn).
        var states = await _practiceService.GetExistingSessionStatesAsync(req.SessionIds, ct);
        var existingIds = states.Select(s => s.SessionId).ToList();
        return Ok(new SessionExistsResponse(existingIds, states));
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

    /// <summary>
    /// E9 — bỏ TOÀN BỘ mốc điểm của tiêu chí nào có thang méo, giữ nguyên các tiêu chí khác.
    ///
    /// Thang méo là loại hỏng KHÔNG có triệu chứng: hai mốc trùng <c>score</c> làm phép snap điểm về
    /// mức (cả phía Python lẫn phía C#) chọn KHÔNG XÁC ĐỊNH; mốc vượt <c>maxScore</c> thì điểm ứng
    /// viên bị neo vào một mức nằm ngoài thang. Cả hai đều ra điểm "trông hợp lệ".
    ///
    /// Bỏ mốc ⇒ tiêu chí đó rơi về dải mặc định 0..maxScore, tức chấm ĐÚNG NHƯ TRƯỚC khi có tính năng
    /// này. Thà chấm như hôm nay còn hơn chấm bằng thang méo. Đây là lá chắn thứ hai — Campaign phải
    /// chặn 400 ngay lúc HR lưu; ở đây fail-soft vì hai service deploy KHÔNG nguyên tử.
    /// </summary>
    private IReadOnlyList<CampaignCriterionInput> SanitizeCriterionLevels(
        IReadOnlyList<CampaignCriterionInput> criteria, Guid campaignId)
    {
        return criteria.Select(c =>
        {
            if (c.Levels is not { Count: > 0 } levels) return c;

            var invalidScore = levels.Where(l => l.Score < 0 || l.Score > c.MaxScore).ToList();
            var duplicated = levels.GroupBy(l => l.Score).Any(g => g.Count() > 1);
            var blankDescriptor = levels.Any(l => string.IsNullOrWhiteSpace(l.Descriptor));
            if (invalidScore.Count == 0 && !duplicated && !blankDescriptor) return c;

            _logger.LogWarning(
                "Campaign {CampaignId}: bỏ mốc điểm của tiêu chí '{Criterion}' vì thang méo "
                + "(ngoài [0,{MaxScore}]: {Invalid}; trùng điểm: {Duplicated}; mô tả rỗng: {Blank}) "
                + "— tiêu chí này rơi về dải mặc định",
                campaignId, c.Name, c.MaxScore,
                string.Join(",", invalidScore.Select(l => l.Score)), duplicated, blankDescriptor);

            return c with { Levels = null };
        }).ToList();
    }
}
