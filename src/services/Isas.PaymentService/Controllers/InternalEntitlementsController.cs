using System.Security.Cryptography;
using System.Text;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;

namespace Isas.PaymentService.Controllers;

[ApiController]
[Route("internal/entitlements")]
public sealed class InternalEntitlementsController : ControllerBase
{
    private readonly EntitlementResolver _resolver;
    private readonly IConfiguration _config;
    public InternalEntitlementsController(EntitlementResolver resolver, IConfiguration config)
        => (_resolver, _config) = (resolver, config);

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAsync([FromQuery] OwnerType ownerType, [FromQuery] Guid ownerId,
        [FromHeader(Name = "X-Internal-Token")] string? token, CancellationToken ct = default)
    {
        var expected = _config["Internal:Token"];
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
            return Unauthorized(new { error = "Invalid internal token" });
        if (ownerId == Guid.Empty) return BadRequest(new { error = "ownerId is required" });
        return Ok(await _resolver.ResolveAsync(ownerType, ownerId, ct));
    }
}
