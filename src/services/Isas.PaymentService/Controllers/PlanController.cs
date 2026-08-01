using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;

namespace Isas.PaymentService.Controllers;

[ApiController]
[Route("admin/plans")]
[Authorize(Roles = "Admin")]
public sealed class PlanController(PlanService plans) : ControllerBase
{
    [HttpGet] public async Task<List<PlanResponse>> GetAsync([FromQuery] PlanAudience? audience, CancellationToken ct) =>
        (await plans.GetAsync(audience, ct)).Select(PlanResponse.From).ToList();
    [HttpGet("{id:guid}")] public async Task<ActionResult<PlanResponse>> GetAsync(Guid id, CancellationToken ct) =>
        await plans.GetAsync(id, ct) is { } plan ? Ok(PlanResponse.From(plan)) : NotFound();
    [HttpPost] public async Task<ActionResult<PlanResponse>> CreateAsync(PlanRequest request, CancellationToken ct)
    {
        try { return Ok(PlanResponse.From(await plans.CreateAsync(request, ct))); } catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
    }
    [HttpPut("{id:guid}")] public async Task<ActionResult<PlanResponse>> UpdateAsync(Guid id, PlanRequest request, CancellationToken ct)
    {
        try { return await plans.UpdateAsync(id, request, ct) is { } plan ? Ok(PlanResponse.From(plan)) : NotFound(); } catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
    }
    [HttpDelete("{id:guid}")] public async Task<IActionResult> DeactivateAsync(Guid id, CancellationToken ct) =>
        await plans.DeactivateAsync(id, ct) ? NoContent() : NotFound();
}
