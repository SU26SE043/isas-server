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

        /// <summary>Cờ xem gói đã ẩn chỉ được tôn trọng khi caller là Admin (role claim khớp A5).</summary>
        private bool CanSeeInactive(bool requested) => requested && User.IsInRole("Admin");

        public PackageController(IPackageService package)
        {
            _package = package;
        }

        // A5 — catalog gói prepaid đang bán là PUBLIC (payment.md:104): không cần đăng nhập để xem giá.
        // BE-D1 — `?includeInactive=true` CHỈ có hiệu lực với Admin (màn Gói & Tier cần thấy gói đã ẩn để
        // "Bán lại"); người lạ/candidate truyền cờ này vẫn chỉ thấy gói đang bán — giá chưa công bố không lộ.
        [HttpGet]
        [AllowAnonymous]
        public async Task<ActionResult<List<PackageResponse>>> GetAllPackageAsync([FromQuery] bool includeInactive = false, CancellationToken ct = default)
        {
            return await _package.GetAllPackagesAsync(CanSeeInactive(includeInactive), ct);
        }

        [HttpGet("{id:guid}")]
        [AllowAnonymous]
        public async Task<ActionResult<PackageResponse>> GetPackageAsync(Guid id, [FromQuery] bool includeInactive = false, CancellationToken ct = default)
        {
            var package = await _package.GetPackageAsync(id, CanSeeInactive(includeInactive), ct);
            if (package is null) return NotFound(new { message = "Package not found" });

            return package;
        }

        // A5 — CRUD gói = admin-only (payment.md §Admin/PlatformAdmin). Role string "Admin" (AUTH-3).
        [HttpPost]
        [Authorize(Roles = "Admin")]
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
        [Authorize(Roles = "Admin")]
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
        [Authorize(Roles = "Admin")]
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
