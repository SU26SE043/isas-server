using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

[ApiController]
[Route("api/practice/sessions")] // Giữ nguyên Route chuẩn này của ông
[Authorize(Roles = "Candidate")] // A5 — luyện phỏng vấn B2C = Candidate.
public class PracticeController : ControllerBase
{
    private readonly IPracticeService _practiceService;
    private readonly ILogger<PracticeController> _logger;

    public PracticeController(IPracticeService practiceService, ILogger<PracticeController> logger)
    {
        _practiceService = practiceService;
        _logger = logger;
    }

    /// <summary>
    /// Helper: Gom code lấy CandidateId từ Token cho gọn, dùng lại ở nhiều hàm
    /// </summary>
    private Guid GetCandidateId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var candidateId))
        {
            throw new UnauthorizedAccessException("Không xác định được danh tính người dùng.");
        }
        return candidateId;
    }

    /// <summary>
    /// 1. Tạo phiên phỏng vấn mới (Gọi AI sinh câu hỏi)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(PracticeSessionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]   // BC2: ví hết credit
    [ProducesResponseType(StatusCodes.Status502BadGateway)]        // BC2: PaymentService down
    public async Task<IActionResult> CreateSession([FromBody] CreatePracticeSessionRequest request, CancellationToken ct)
    {

        try
        {
            var candidateId = GetCandidateId();
            _logger.LogInformation("Candidate {CandidateId} đang yêu cầu tạo Session phỏng vấn cho Job {JobCategory}", 
                candidateId, request.JobCategory);

            var sessionResult = await _practiceService.CreateSessionAsync(candidateId, request, ct);

            // Trả về HTTP 201 Created kèm dữ liệu Session
            return Created($"/api/practice/sessions/{sessionResult.Id}", sessionResult);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InsufficientCreditException ex)
        {
            // BC2: ví hết credit → 402, KHÔNG tạo session (reserve ném trước khi ghi row).
            _logger.LogWarning(ex, "Ví không đủ credit để tạo session luyện.");
            return StatusCode(StatusCodes.Status402PaymentRequired, new { error = ex.Message });
        }
        catch (PaymentServiceException ex)
        {
            // BC2: PaymentService không phản hồi → 502 (không tạo session; retry được).
            _logger.LogError(ex, "PaymentService lỗi khi reserve credit.");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Dịch vụ thanh toán tạm thời không phản hồi. Vui lòng thử lại sau." });
        }
        catch (AiServiceException ex)
        {
            // AIService lỗi thật (transport/timeout/5xx khi sinh câu hỏi) → 502, KHÔNG phải 400: đây là
            // lỗi upstream, không phải lỗi request. Reserve credit đã được release ở service (P1-2/BK12).
            _logger.LogError(ex, "AIService lỗi khi sinh câu hỏi.");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Dịch vụ AI tạm thời không phản hồi. Vui lòng thử lại sau." });
        }
        catch (InvalidOperationException ex)
        {
            // Bắt lỗi AI trả về rỗng, hoặc CV/JD không đọc được → 400 (lỗi input, không phải upstream).
            _logger.LogWarning(ex, "Lỗi logic khi tạo session.");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi nghiêm trọng khi tạo Practice Session");
            return StatusCode(500, new { error = "Hệ thống AI đang quá tải hoặc gặp lỗi. Vui lòng thử lại sau." });
        }
    }

    /// <summary>
    /// 2. Lấy danh sách lịch sử phỏng vấn của user (Đặt trước {sessionId} để không bị lỗi Route)
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IReadOnlyList<PracticeSessionSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(CancellationToken ct)
    {
        try
        {
            var candidateId = GetCandidateId();
            var history = await _practiceService.GetHistoryAsync(candidateId, ct);
            return Ok(history);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 3. Lấy thông tin chi tiết một phiên phỏng vấn (Câu hỏi, bài nộp, điểm số)
    /// </summary>
    [HttpGet("{sessionId:guid}")]
    [ProducesResponseType(typeof(PracticeSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSession(Guid sessionId, CancellationToken ct)
    {
        try
        {
            var candidateId = GetCandidateId();
            var response = await _practiceService.GetSessionAsync(candidateId, sessionId, ct);
            
            if (response == null)
                return NotFound(new { error = "Không tìm thấy phiên phỏng vấn này." });

            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 4. Chốt sổ và nộp bài phỏng vấn (Bắn RabbitMQ đi chấm)
    /// </summary>
    [HttpPost("{sessionId:guid}/submit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitSession(Guid sessionId, CancellationToken ct)
    {
        try
        {
            var candidateId = GetCandidateId();
            await _practiceService.SubmitSessionAsync(candidateId, sessionId, ct);
            
            return NoContent(); // HTTP 204
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Lỗi chưa nộp câu nào, hoặc đã nộp rồi...
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }
}