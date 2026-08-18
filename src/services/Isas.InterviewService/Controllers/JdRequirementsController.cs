using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Isas.InterviewService.Controllers;

[ApiController]
[Route("api/practice/jd-requirements")]
[Authorize(Roles = "Candidate")]
[EnableRateLimiting("JdRequirements")]
public class JdRequirementsController(
    IJdRequirementService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(JdRequirementsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Suggest(
        [FromBody] JdRequirementsRequest request, CancellationToken ct)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var candidateId))
            return Unauthorized(new { error = "Không xác định được danh tính người dùng." });

        try
        {
            return Ok(await service.SuggestAsync(candidateId, request, ct));
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
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}
