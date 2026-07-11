using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

// BC7 — /api/v1/interview/practice/cv-analysis (gateway strip → api/practice/cv-analysis).
[ApiController]
[Route("api/practice/cv-analysis")]
[Authorize]
public class CvAnalysisController : ControllerBase
{
    private readonly ICvAnalysisService _service;
    private readonly ILogger<CvAnalysisController> _logger;

    public CvAnalysisController(ICvAnalysisService service, ILogger<CvAnalysisController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private bool TryGetCandidateId(out Guid candidateId)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out candidateId);
    }

    // POST /cv-analysis {cvId, jdId?, jobCategory} → 201 CvAnalysisResponse.
    [HttpPost]
    [ProducesResponseType(typeof(CvAnalysisResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]   // BC7b: ví hết credit
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Analyze([FromBody] CvAnalysisRequest request, CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            var result = await _service.AnalyzeAsync(candidateId, request, ct);
            return Created($"/api/practice/cv-analysis/{result.Id}", result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InsufficientCreditException ex)
        {
            // BC7b: ví hết credit → 402, KHÔNG gọi AI/tạo row (reserve ném trước — PAY-5).
            _logger.LogWarning(ex, "Ví không đủ credit để phân tích CV cho {CandidateId}", candidateId);
            return StatusCode(StatusCodes.Status402PaymentRequired, new { error = ex.Message });
        }
        catch (PaymentServiceException ex)
        {
            // BC7b: PaymentService không phản hồi khi reserve → 502 (retry được; không tạo row).
            _logger.LogError(ex, "PaymentService lỗi khi reserve credit phân tích CV cho {CandidateId}", candidateId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Dịch vụ thanh toán tạm thời không phản hồi. Vui lòng thử lại sau." });
        }
        catch (AiServiceException ex)
        {
            _logger.LogWarning(ex, "AIService /analyze-cv lỗi khi phân tích CV cho {CandidateId}", candidateId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "AIService gặp lỗi. Vui lòng thử lại sau." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET /cv-analysis/{id} → CvAnalysisResponse (chỉ chủ; khác chủ → 403; không có → 404).
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CvAnalysisResponse), StatusCodes.Status200OK)]
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
                return NotFound(new { error = "Không tìm thấy phân tích CV này." });

            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    // GET /cv-analysis → CvAnalysisResponse[] của chính user.
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CvAnalysisResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetCandidateId(out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        var results = await _service.ListAsync(candidateId, ct);
        return Ok(results);
    }
}
