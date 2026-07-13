using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

[ApiController]
[Authorize(Roles = "Candidate")] // A5 — upload answer B2C = Candidate; 2 callback internal giữ [AllowAnonymous].
public class AnswersController : ControllerBase   // KHÔNG [Route] cấp class
{
    private readonly IAnswerService _answerService;
    private readonly IConfiguration _config;
    private readonly ILogger<AnswersController> _logger;

    public AnswersController(
        IAnswerService answerService,
        IConfiguration config,
        ILogger<AnswersController> logger)
    {
        _answerService = answerService;
        _config = config;
        _logger = logger;
    }

    // Upload audio — route đầy đủ trên action.
    [HttpPost("api/practice/sessions/{sessionId:guid}/answers")]
    [RequestSizeLimit(52_428_800)] // 50MB
    public async Task<IActionResult> Upload(
        Guid sessionId,
        [FromForm] Guid questionId,
        IFormFile file,
        [FromForm] int durationSec,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Thiếu file audio" });

        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var candidateId))
            return Unauthorized();

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _answerService.UploadAnswerAsync(
                sessionId, questionId, candidateId,
                stream, file.ContentType, durationSec, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload answer failed for session {SessionId} question {QuestionId}", sessionId, questionId);
            return StatusCode(500, new { error = "Upload answer thất bại" });
        }
    }

    // Callback INTERNAL — route đầy đủ, không phụ thuộc prefix class.
    // AllowAnonymous vì gọi máy-máy; xác thực bằng X-Internal-Token.
    [HttpPost("internal/answers/{answerId:guid}/result")]
    [AllowAnonymous]
    public async Task<IActionResult> SaveResult(
        Guid answerId,
        [FromBody] AnswerScoreCallbackRequest req,
        [FromHeader(Name = "X-Internal-Token")] string? token,
        CancellationToken ct)
    {
        if (!IsValidInternalToken(token, answerId))
            return Unauthorized(new { error = "Invalid internal token" });

        try
        {
            await _answerService.SaveResultAsync(answerId, req, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // Callback INTERNAL — worker báo chấm thất bại vĩnh viễn -> đánh dấu Failed.
    // Nhờ vậy session đang chờ chấm không bị kẹt Scoring mãi.
    [HttpPost("internal/answers/{answerId:guid}/failed")]
    [AllowAnonymous]
    public async Task<IActionResult> MarkFailed(
        Guid answerId,
        [FromBody] AnswerFailedCallbackRequest req,
        [FromHeader(Name = "X-Internal-Token")] string? token,
        CancellationToken ct)
    {
        if (!IsValidInternalToken(token, answerId))
            return Unauthorized(new { error = "Invalid internal token" });

        try
        {
            await _answerService.MarkFailedAsync(answerId, req.Reason, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private bool IsValidInternalToken(string? token, Guid answerId)
    {
        var expected = _config["Internal:Token"];
        if (string.IsNullOrEmpty(expected) || token != expected)
        {
            _logger.LogWarning("Callback bị từ chối: token sai cho answer {AnswerId}", answerId);
            return false;
        }
        return true;
    }
}