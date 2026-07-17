using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Isas.Shared.Pagination;
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

        // GET /auth/admin/organizations — mọi org (mới nhất trước; keyset-paged DB8); ?search= lọc theo Name.
        // ?limit= (mặc định/tối đa 500) + ?cursor= (opaque); next-cursor ở header X-Next-Cursor (vắng = hết
        // trang). Body giữ nguyên mảng JSON (backward-compat cho FE).
        [HttpGet("organizations")]
        public async Task<ActionResult<IReadOnlyList<OrganizationResponse>>> ListOrganizations(
            [FromQuery] string? search = null,
            [FromQuery] string? cursor = null, [FromQuery] int? limit = null, CancellationToken ct = default)
        {
            var page = await _authService.ListAllOrganizationsAsync(search, cursor, limit, ct);
            if (page.NextCursor is not null)
                Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
            return Ok(page.Items);
        }

        // GET /auth/admin/users — mọi user (mới nhất trước; keyset-paged DB8); ?role= lọc platform-role, ?search= lọc email.
        // ?limit= (mặc định/tối đa 500) + ?cursor= (opaque); next-cursor ở header X-Next-Cursor (vắng = hết
        // trang). Body giữ nguyên mảng JSON (backward-compat cho FE).
        [HttpGet("users")]
        public async Task<ActionResult<IReadOnlyList<AdminUserResponse>>> ListUsers(
            [FromQuery] string? role = null, [FromQuery] string? search = null,
            [FromQuery] string? cursor = null, [FromQuery] int? limit = null, CancellationToken ct = default)
        {
            var page = await _authService.ListAllUsersAsync(role, search, cursor, limit, ct);
            if (page.NextCursor is not null)
                Response.Headers[KeysetPaging.NextCursorHeader] = page.NextCursor;
            return Ok(page.Items);
        }
    }
}
