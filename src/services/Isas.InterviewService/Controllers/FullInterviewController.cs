using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

[ApiController]
[Route("api/practice")]
[Authorize]
public class FullInterviewController(IPracticeService practice) : ControllerBase
{
    // Lấy userId từ JWT (sub / nameidentifier)
    private Guid UserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException("Thiếu user id trong token."));

    // ---------- Phase 2: tạo phiên ----------
    [HttpPost("sessions")]
    public async Task<ActionResult<PracticeSessionResponse>> Create(
        [FromBody] CreatePracticeSessionRequest request, CancellationToken ct)
    {
        var result = await practice.CreateSessionAsync(UserId, request, ct);
        return CreatedAtAction(nameof(GetById), new { sessionId = result.Id }, result);
    }

    // ---------- Phase 3: sinh câu hỏi ----------
    [HttpPost("sessions/{sessionId:guid}/questions")]
    public async Task<ActionResult<PracticeSessionResponse>> GenerateQuestions(
        Guid sessionId, CancellationToken ct)
    {
        var result = await practice.GenerateQuestionsAsync(UserId, sessionId, ct);
        return Ok(result);
    }

    // ---------- Phase 4: trả lời 1 câu ----------
    [HttpPost("sessions/{sessionId:guid}/answers")]
    public async Task<ActionResult<AnswerResponse>> SubmitAnswer(
        Guid sessionId, [FromBody] SubmitAnswerRequest request, CancellationToken ct)
    {
        var result = await practice.SubmitAnswerAsync(UserId, sessionId, request, ct);
        return Ok(result);
    }

    // ---------- Phase 5: submit toàn phiên ----------
    [HttpPost("sessions/{sessionId:guid}/submit")]
    public async Task<IActionResult> Submit(Guid sessionId, CancellationToken ct)
    {
        await practice.SubmitSessionAsync(UserId, sessionId, ct);
        return Accepted(); // 202: đã nhận, đang chấm async
    }

    // ---------- Phase 6: chấm lại nếu failed ----------
    // [HttpPost("sessions/{sessionId:guid}/resubmit")]
    // public async Task<IActionResult> Resubmit(Guid sessionId, CancellationToken ct)
    // {
    //     await practice.ResubmitSessionAsync(UserId, sessionId, ct);
    //     return Accepted();
    // }

    // ---------- Phase 4/6: xem chi tiết 1 phiên ----------
    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<ActionResult<PracticeSessionResponse>> GetById(
        Guid sessionId, CancellationToken ct)
    {
        var result = await practice.GetSessionAsync(UserId, sessionId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    // ---------- Phase 6: lịch sử ----------
    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<PracticeSessionSummary>>> GetHistory(
        CancellationToken ct)
    {
        var result = await practice.GetHistoryAsync(UserId, ct);
        return Ok(result);
    }
}