using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using System.Security.Claims;

namespace Isas.PaymentService.Controllers
{
    [ApiController]
    [Route("package")]
    public class PackageController : Controller
    {
        private readonly IPackageService _package;

        public PackageController(IPackageService package)
        {
            _package = package;
        }

        [HttpGet]
        //[Authorize(Roles = "Admin")]
        public async Task<ActionResult<List<PackageResponse>>> GetAllPackageAsync(CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            return await _package.GetAllPackagesAsync(ct);
        }

        [HttpGet("{id:guid}")]
        //[Authorize(Roles = "Admin")]
        public async Task<ActionResult<PackageResponse>> GetPackageAsync(Guid id, CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            return await _package.GetPackageAsync(id, ct);
        }

        [HttpPost]
        //[Authorize(Roles = "Admin")]
        public async Task<ActionResult<PackageResponse>> CreatePackageAsync(CreatePackageRequest request, CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            try
            {
                var package = await _package.CreatePackageAsync(request, ct);
                return package;
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("{id:guid}")]
        //[Authorize(Roles = "Admin")]
        public async Task<ActionResult<PackageResponse>> UpdatePackageAsync(Guid id, UpdatePackageRequest request, CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            try
            {
                var result = await _package.UpdatePackageAsync(id, request, ct);
                if (result is null) return NotFound("Package not found");
                return result;
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("{id:guid}")]
        //[Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeletePackageAsync(Guid id, CancellationToken ct = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            var result = await _package.DeletePackageAsync(id, ct);
            if (!result)
                return NotFound();

            return NoContent();
        }
    }
}
