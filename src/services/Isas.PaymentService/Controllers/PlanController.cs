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
    [HttpGet] public Task<List<Plan>> GetAsync([FromQuery] PlanAudience? audience, CancellationToken ct) => plans.GetAsync(audience, ct);
    [HttpGet("{id:guid}")] public async Task<ActionResult<Plan>> GetAsync(Guid id, CancellationToken ct) =>
        await plans.GetAsync(id, ct) is { } plan ? Ok(plan) : NotFound();
    [HttpPost] public async Task<ActionResult<Plan>> CreateAsync(PlanRequest request, CancellationToken ct)
    {
        try { return Ok(await plans.CreateAsync(request, ct)); } catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
    }
    [HttpPut("{id:guid}")] public async Task<ActionResult<Plan>> UpdateAsync(Guid id, PlanRequest request, CancellationToken ct)
    {
        try { return await plans.UpdateAsync(id, request, ct) is { } plan ? Ok(plan) : NotFound(); } catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
    }
    [HttpDelete("{id:guid}")] public async Task<IActionResult> DeactivateAsync(Guid id, CancellationToken ct) =>
        await plans.DeactivateAsync(id, ct) ? NoContent() : NotFound();
}
