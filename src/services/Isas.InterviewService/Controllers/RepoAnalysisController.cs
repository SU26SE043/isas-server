using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

[ApiController]
[Route("api/practice/repo-analysis")]
[Authorize(Roles = "Candidate")]
public class RepoAnalysisController(IRepoAnalysisService service) : ControllerBase
{
    private bool Candidate(out Guid id) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out id);
    [HttpPost]
    public async Task<IActionResult> Analyze([FromBody] RepoAnalysisRequest request, CancellationToken ct)
    {
        if (!Candidate(out var id)) return Unauthorized();
        try { var result=await service.AnalyzeAsync(id,request,ct); return Created($"/api/practice/repo-analysis/{result.Id}",result); }
        catch (InsufficientCreditException e) { return StatusCode(402,new {error=e.Message}); }
        catch (KeyNotFoundException e) { return NotFound(new {error=e.Message}); }
        catch (UnauthorizedAccessException e) { return StatusCode(403,new {error=e.Message}); }
        catch (AiServiceException e) { return StatusCode(502,new {error=e.Message}); }
        catch (PaymentServiceException e) { return StatusCode(502,new {error=e.Message}); }
        catch (InvalidOperationException e) { return BadRequest(new {error=e.Message}); }
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) { if(!Candidate(out var candidate))return Unauthorized(); try { var result=await service.GetAsync(candidate,id,ct); return result is null?NotFound():Ok(result); } catch(UnauthorizedAccessException e) { return StatusCode(403,new {error=e.Message}); } }
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct, [FromQuery]string? cursor=null,[FromQuery]int? limit=null) { if(!Candidate(out var id))return Unauthorized(); var page=await service.ListAsync(id,cursor,limit,ct); if(page.NextCursor is not null)Response.Headers["X-Next-Cursor"]=page.NextCursor; return Ok(page.Items); }
}
