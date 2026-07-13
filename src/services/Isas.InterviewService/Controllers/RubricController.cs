using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

/// <summary>
/// BC16 — Rubric CÁ NHÂN B2C: candidate tự CRUD bộ tiêu chí luyện tập theo JobCategory (KHÔNG admin).
/// Owner-scope tuyệt đối: mọi thao tác khoá theo candidateId trong JWT — không đụng rubric người khác.
/// </summary>
[ApiController]
[Route("api/practice/rubrics")]
[Authorize(Roles = "Candidate")]   // A5 — luyện tập B2C = Candidate.
public class RubricController : ControllerBase
{
    private readonly IRubricLibraryService _service;
    private readonly ILogger<RubricController> _logger;

    public RubricController(IRubricLibraryService service, ILogger<RubricController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private Guid GetCandidateId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var id))
            throw new UnauthorizedAccessException("Không xác định được danh tính người dùng.");
        return id;
    }

    /// <summary>Rubric hiệu lực cho 1 nghề: rubric riêng nếu có, else seed mặc định (template).</summary>
    [HttpGet("{jobCategory}")]
    [ProducesResponseType(typeof(RubricResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(JobCategory jobCategory, CancellationToken ct)
    {
        try
        {
            return Ok(await _service.GetEffectiveAsync(GetCandidateId(), jobCategory, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>Thay toàn bộ rubric riêng cho 1 nghề (replace-all). Σweight ngoài [0.99,1.01] / rỗng → 400.</summary>
    [HttpPut("{jobCategory}")]
    [ProducesResponseType(typeof(RubricResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Replace(
        JobCategory jobCategory, [FromBody] UpsertRubricRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await _service.ReplaceAsync(GetCandidateId(), jobCategory, request, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Xoá rubric riêng cho 1 nghề → quay về seed mặc định. Idempotent → 204.</summary>
    [HttpDelete("{jobCategory}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reset(JobCategory jobCategory, CancellationToken ct)
    {
        try
        {
            await _service.ResetAsync(GetCandidateId(), jobCategory, ct);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
