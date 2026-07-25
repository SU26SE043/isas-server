using System.Security.Cryptography;
using System.Text;
using Isas.PaymentService.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;

namespace Isas.PaymentService.Controllers;

[ApiController]
[Route("internal/http-traffic")]
public sealed class InternalHttpTrafficController(PaymentDbContext db, IConfiguration config, ILogger<InternalHttpTrafficController> logger) : ControllerBase
{
    [HttpPost, AllowAnonymous]
    public async Task<IActionResult> Record(RecordHttpTrafficRequest req, [FromHeader(Name = "X-Internal-Token")] string? token, CancellationToken ct)
    {
        var expected = config["Internal:Token"];
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token))) return Unauthorized(new { error = "Invalid internal token" });
        try { db.HttpTrafficStats.Add(new HttpTrafficStat { Id = Guid.NewGuid(), WindowStart = req.WindowStart, WindowEnd = req.WindowEnd, RouteId = req.RouteId, StatusClass = req.StatusClass, Requests = req.Requests, SumDurationMs = req.SumDurationMs, MaxDurationMs = req.MaxDurationMs, CreatedAt = DateTime.UtcNow }); await db.SaveChangesAsync(ct); return Accepted(); }
        catch (Exception ex) { logger.LogError(ex, "FR18 traffic dropped"); return Accepted(new { status = "dropped" }); }
    }
}
