using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
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

        // Cổng định dạng. ⚠ PHẢI đứng SAU khối parse `sub` ở trên, đừng dời lên trước:
        //   (a) hai test 401 (`Upload_MissingSubClaim_401`, `Upload_UnparsableSubClaim_401`) chỉ dựng `Length`
        //       nên `ContentType` là null — dời lên sẽ biến 401 thành 400;
        //   (b) smoke của CI (`scripts/verify-gateway-openapi.py`) POST body `{}` vào MỌI endpoint trong doc
        //       mỗi lần deploy, và nó phải tiếp tục dừng ở 401 của tầng auth.
        var contentType = file.ContentType;
        if (_config.GetValue("Audio:StrictFormatGate", true))
        {
            // Đọc riêng vài byte đầu: `OpenReadStream()` trả stream mới mỗi lần gọi (thân request đã được
            // buffer) nên lần đọc này không tiêu mất dữ liệu của lần đọc thật bên dưới.
            var head = new byte[12];
            int read;
            await using (var probe = file.OpenReadStream())
                read = await probe.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);

            if (!AudioFormats.TryResolve(head.AsSpan(0, read), file.ContentType, file.FileName,
                    out var canonicalMime, out _, out var source))
            {
                _logger.LogWarning(
                    "Từ chối audio không nhận dạng được (session {SessionId}, contentType {ContentType}, tên {FileName})",
                    sessionId, file.ContentType, file.FileName);
                return BadRequest(new
                {
                    error = $"Định dạng audio không hỗ trợ. Chấp nhận: {AudioFormats.AcceptedList}."
                });
            }

            if (source != AudioFormatSource.MagicBytes)
            {
                // Quan sát sau deploy: nội dung file lẽ ra luôn tự khai được. Rơi vào đây nghĩa là có client
                // gửi định dạng ta chưa lường — biết sớm, trước khi nó thành sự cố.
                _logger.LogWarning(
                    "Định dạng audio suy từ {Source} chứ không phải nội dung file (session {SessionId}, contentType {ContentType}, tên {FileName})",
                    source, sessionId, file.ContentType, file.FileName);
            }

            contentType = canonicalMime;
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _answerService.UploadAnswerAsync(
                sessionId, questionId, candidateId,
                stream, contentType, durationSec, ct);
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
            await _answerService.MarkFailedAsync(answerId, req.Reason, req.NoSpeech, ct);
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
        // Fail-closed: token chưa cấu hình → từ chối hết (không mở toang). Loại token null/rỗng sớm.
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Callback bị từ chối: token sai cho answer {AnswerId}", answerId);
            return false;
        }

        // So khớp HẰNG-THỜI-GIAN trên UTF-8 bytes — ranh giới auth DUY NHẤT cho callback ghi điểm
        // (máy-máy). `token != expected` rò rỉ timing (thoát sớm ở byte lệch đầu tiên).
        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
        if (!ok)
            _logger.LogWarning("Callback bị từ chối: token sai cho answer {AnswerId}", answerId);
        return ok;
    }
}