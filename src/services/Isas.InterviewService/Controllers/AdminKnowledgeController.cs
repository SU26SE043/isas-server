using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers;

// RAG grounding — quản nguồn tri thức (Admin). Base /api/admin/knowledge → FE {apiBase}/interview/admin/knowledge
// (gateway route interview/admin/** đã có). Surface RIÊNG khỏi F21 (F21 = lời văn prompt; đây = nguồn).
[ApiController]
[Route("api/admin/knowledge")]
[Authorize(Roles = "Admin")]
public class AdminKnowledgeController(IKnowledgeService service) : ControllerBase
{
    private bool Admin(out Guid id)
        => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out id);

    // GET /api/admin/knowledge?jobCategory=&cursor=&limit= — list keyset paged (mẫu DB8).
    [HttpGet]
    public async Task<IActionResult> List(
        CancellationToken ct,
        [FromQuery] JobCategory? jobCategory = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int? limit = null)
    {
        var page = await service.ListAsync(jobCategory, cursor, limit, ct);
        if (page.NextCursor is not null) Response.Headers["X-Next-Cursor"] = page.NextCursor;
        return Ok(page.Items);
    }

    // POST /api/admin/knowledge — nạp nguồn Manual/Url → 201.
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateKnowledgeRequest req, CancellationToken ct)
    {
        if (!Admin(out var adminId)) return Unauthorized();
        try
        {
            var result = await service.IngestAsync(adminId, req, ct);
            return Created($"/api/admin/knowledge/{result.Id}", result);
        }
        catch (Context7RateLimitException e) { if (e.RetryAfter is not null) Response.Headers["Retry-After"] = e.RetryAfter; return StatusCode(429, new { error = e.Message }); }
        catch (Context7Exception e) { return StatusCode(502, new { error = e.Message }); }
        catch (AiServiceException e) { return StatusCode(502, new { error = e.Message }); }
        catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
    }

    // DELETE /api/admin/knowledge/{id} — xóa Qdrant point trước, row sau → 204 / 404.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var ok = await service.DeleteAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    // POST /api/admin/knowledge/{id}/reindex — re-chunk + re-embed → 200 KnowledgeSource / 404.
    [HttpPost("{id:guid}/reindex")]
    public async Task<IActionResult> Reindex(Guid id, CancellationToken ct)
    {
        try
        {
            var result = await service.ReindexAsync(id, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (Context7RateLimitException e) { if (e.RetryAfter is not null) Response.Headers["Retry-After"] = e.RetryAfter; return StatusCode(429, new { error = e.Message }); }
        catch (Context7Exception e) { return StatusCode(502, new { error = e.Message }); }
        catch (AiServiceException e) { return StatusCode(502, new { error = e.Message }); }
        catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
    }

    // GET /api/admin/knowledge/context7/search?libraryName=&query= — proxy Context7 /libs/search.
    [HttpGet("context7/search")]
    public async Task<IActionResult> Context7Search(
        [FromQuery] string libraryName, CancellationToken ct, [FromQuery] string? query = null)
    {
        if (string.IsNullOrWhiteSpace(libraryName)) return BadRequest(new { error = "libraryName là bắt buộc." });
        try
        {
            var results = await service.Context7SearchAsync(libraryName, query, ct);
            return Ok(results);
        }
        catch (Context7RateLimitException e) { if (e.RetryAfter is not null) Response.Headers["Retry-After"] = e.RetryAfter; return StatusCode(429, new { error = e.Message }); }
        catch (Context7Exception e) { return StatusCode(502, new { error = e.Message }); }
    }

    // POST /api/admin/knowledge/context7/ingest — nạp N topic của 1 thư viện Context7 → 201.
    [HttpPost("context7/ingest")]
    public async Task<IActionResult> Context7Ingest([FromBody] Context7IngestRequest req, CancellationToken ct)
    {
        if (!Admin(out var adminId)) return Unauthorized();
        try
        {
            var result = await service.Context7IngestAsync(adminId, req, ct);
            return Created($"/api/admin/knowledge/{result.Id}", result);
        }
        catch (Context7RateLimitException e) { if (e.RetryAfter is not null) Response.Headers["Retry-After"] = e.RetryAfter; return StatusCode(429, new { error = e.Message }); }
        catch (Context7Exception e) { return StatusCode(502, new { error = e.Message }); }
        catch (AiServiceException e) { return StatusCode(502, new { error = e.Message }); }
        catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
    }
}
