using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

/// <summary>B2C coaching (2026-09-17) — kiểm mặt định kỳ trong buổi luyện (đếm mặt, detect-only).</summary>
[ApiController]
[Authorize(Roles = "Candidate")]
public class PracticeFaceCheckController : ControllerBase
{
    private readonly IPracticeFaceCheckService _service;
    private readonly ILogger<PracticeFaceCheckController> _logger;

    public PracticeFaceCheckController(
        IPracticeFaceCheckService service, ILogger<PracticeFaceCheckController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private Guid GetCandidateId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var candidateId))
            throw new UnauthorizedAccessException("Không xác định được danh tính người dùng.");
        return candidateId;
    }

    /// <summary>
    /// 204 = "không áp dụng" (buổi tắt theo dõi / B2B / đã kết thúc) — KHÔNG upload, KHÔNG gọi AI.
    /// 200 = có kết quả (kể cả khi không phát hiện tín hiệu nào — signals rỗng).
    /// </summary>
    [HttpPost("api/practice/sessions/{sessionId:guid}/face-check")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2_097_152)] // 2MB — khớp trần ảnh của PracticeFaceCheckService
    [ProducesResponseType(typeof(FaceCheckResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> RecordFaceCheck(
        Guid sessionId, [FromForm] IFormFile image, CancellationToken ct)
    {
        if (image is null)
            return BadRequest(new { error = "Thiếu ảnh." });

        try
        {
            await using var stream = image.OpenReadStream();
            var result = await _service.RecordFaceCheckAsync(
                GetCandidateId(), sessionId, stream, image.Length, ct);

            return result is null ? NoContent() : Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (AiServiceException ex)
        {
            // Ảnh + dòng sổ ĐÃ ghi trước khi gọi AI (xem PracticeFaceCheckService) — ném ra ngoài
            // an toàn, không mồ côi. Tách 504 (hết giờ) khỏi 502 như TTS/decide-next (báo cáo
            // 2026-08-15: gộp một mã khiến client chỉ thấy "có lỗi", không phân biệt được nên
            // thử lại hay không).
            _logger.LogError(ex, "AIService lỗi khi kiểm mặt (timeout={IsTimeout}).", ex.IsTimeout);
            return ex.IsTimeout
                ? StatusCode(StatusCodes.Status504GatewayTimeout,
                    new { error = "Dịch vụ kiểm mặt phản hồi chậm. Bạn thử lại lượt sau nhé." })
                : StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "Dịch vụ kiểm mặt tạm thời không phản hồi." });
        }
    }
}
