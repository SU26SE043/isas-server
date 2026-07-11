using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

// BC12 (D20) — /api/v1/interview/practice/roadmaps (gateway strip → api/practice/roadmaps).
// Vòng này CHỈ POST (tạo) + GET (đọc cấu trúc). Lesson theory lazy-gen + /start reserve → BC14.
[ApiController]
[Route("api/practice/roadmaps")]
[Authorize]
public class RoadmapsController : ControllerBase
{
    private readonly IRoadmapService _service;
    private readonly ILogger<RoadmapsController> _logger;

    public RoadmapsController(IRoadmapService service, ILogger<RoadmapsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private bool TryGetCandidateId(out Guid candidateId)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out candidateId);
    }

    // POST /roadmaps {jobCategory, level, cvId?} → 201 RoadmapResponse (không trừ credit).
    [HttpPost]
    [ProducesResponseType(typeof(RoadmapResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Create([FromBody] CreateRoadmapRequest request, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var result = await _service.CreateAsync(candidateId, request, ct);
            return Created($"/api/practice/roadmaps/{result.Id}", result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (AiServiceException ex)
        {
            _logger.LogWarning(ex, "AIService /generate-roadmap lỗi khi tạo roadmap cho {CandidateId}", candidateId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "AIService gặp lỗi. Vui lòng thử lại sau." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET /roadmaps/{id} → RoadmapResponse đầy đủ (chỉ chủ; khác chủ → 403; không có → 404).
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RoadmapResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var result = await _service.GetAsync(candidateId, id, ct);
            if (result is null)
                return NotFound(new { error = "Không tìm thấy roadmap này." });

            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    // GET /roadmaps → RoadmapResponse[] của chính user (không kèm theoryContent).
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoadmapResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        var results = await _service.ListAsync(candidateId, ct);
        return Ok(results);
    }
}
