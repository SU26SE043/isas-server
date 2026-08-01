using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.PaymentService.Controllers;
[ApiController, Route("admin/subscriptions"), Authorize(Roles = "Admin")]
public sealed class AdminSubscriptionsController(ISubscriptionService subscriptions) : ControllerBase
{
    [HttpPost("grant")]
    public async Task<IActionResult> Grant(GrantSubscriptionRequest request, CancellationToken ct) {
        try { return Ok(await subscriptions.GrantAsync(request.OwnerType, request.OwnerId, request.PlanId, request.DurationDays, request.ActivatedAt, request.IdempotencyKey, ct)); }
        catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
    }
}
