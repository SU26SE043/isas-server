using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
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
            req.CampaignId, jobCategory, req.Questions, req.Criteria);

        try
        {
            var result = await _practiceService.GetOrCreateCampaignSessionAsync(req.CandidateId, request, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "create-or-get campaign session lỗi input (candidate {CandidateId}, campaign {CampaignId})",
                req.CandidateId, req.CampaignId);
            return BadRequest(new { error = ex.Message });
        }
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
