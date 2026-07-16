using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.AuthService.Controllers
{
    /// <summary>
    /// AUTH-7 — PlatformAdmin oversight (read-only, cross-org). Xem MỌI org + MỌI user toàn hệ thống.
    /// Admin-gated trong service sở hữu dữ liệu (không phải service riêng). Không mutation.
    /// </summary>
    [ApiController]
    [Route("auth/admin")]
    [Authorize(Roles = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AdminController(IAuthService authService)
        {
            _authService = authService;
        }

        // GET /auth/admin/organizations — mọi org (cap 500, mới nhất trước); ?search= lọc theo Name.
        [HttpGet("organizations")]
        public async Task<ActionResult<IReadOnlyList<OrganizationResponse>>> ListOrganizations(
            [FromQuery] string? search = null, CancellationToken ct = default)
        {
            var orgs = await _authService.ListAllOrganizationsAsync(search, ct);
            return Ok(orgs);
        }

        // GET /auth/admin/users — mọi user (cap 500, mới nhất trước); ?role= lọc platform-role, ?search= lọc email.
        [HttpGet("users")]
        public async Task<ActionResult<IReadOnlyList<AdminUserResponse>>> ListUsers(
            [FromQuery] string? role = null, [FromQuery] string? search = null, CancellationToken ct = default)
        {
            var users = await _authService.ListAllUsersAsync(role, search, ct);
            return Ok(users);
        }
    }
}
