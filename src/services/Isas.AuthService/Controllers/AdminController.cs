using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.AuthService.Controllers
{
    /// <summary>
    /// AUTH-7 — PlatformAdmin oversight (cross-org). Xem MỌI org + MỌI user toàn hệ thống, và (F20)
    /// can thiệp lên account: đình chỉ / gỡ đình chỉ / đặt lại mật khẩu hộ.
    /// Admin-gated trong service sở hữu dữ liệu (không phải service riêng).
    ///
    /// ⚠ KHÁC <see cref="OrgMembersController"/>: ở đó quyền là <c>Employer</c> + <c>OrgAdmin</c>
    /// TRONG một org (AUTH-4/AUTH-8); ở đây là platform-role <c>Admin</c>, phạm vi toàn hệ thống.
    /// Đừng bê mô hình quyền của bên kia sang.
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

        // POST /auth/admin/users/{userId}/ban — đình chỉ account (F20/FR16).
        // Tự ban mình → 400; user lạ → 404; Admin hoạt động cuối cùng → 409.
        //
        // ⚠ Hiệu lực KHÔNG tức thì với access token đang lưu hành (GEN-3: service khác validate JWT
        // offline). Ban chặn phát phiên mới + thu hồi refresh token → account chết hẳn sau ≤1 TTL
        // access (15'). Xem AuthService.BanUserAsync + docs/services/auth.md.
        [HttpPost("users/{userId:guid}/ban")]
        public async Task<ActionResult<AdminUserResponse>> BanUser(
            Guid userId, BanUserRequest? request, CancellationToken ct = default)
        {
            var actingAdminId = User.GetUserId();
            if (actingAdminId is null)
                return Forbid();

            // Tự ban mình = tự khoá mình ra ngoài, gần như luôn là thao tác nhầm (mẫu A6b "tự xoá mình").
            if (actingAdminId.Value == userId)
                return BadRequest(new { error = "Cannot ban yourself" });

            try
            {
                return Ok(await _authService.BanUserAsync(actingAdminId.Value, userId, request?.Reason, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (AdminActionConflictException ex)
            {
                return Conflict(new { error = ex.Message });
            }
        }

        // POST /auth/admin/users/{userId}/unban — gỡ đình chỉ. User lạ → 404.
        [HttpPost("users/{userId:guid}/unban")]
        public async Task<ActionResult<AdminUserResponse>> UnbanUser(Guid userId, CancellationToken ct = default)
        {
            try
            {
                return Ok(await _authService.UnbanUserAsync(userId, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        // POST /auth/admin/users/{userId}/reset-password — đặt lại mật khẩu hộ user.
        // User lạ → 404; mật khẩu không đạt policy Identity → 400. Thành công → 204 (không trả lại
        // mật khẩu trong response: admin đã có nó, echo lại chỉ thêm một bản sao trong log/history).
        [HttpPost("users/{userId:guid}/reset-password")]
        public async Task<IActionResult> ResetUserPassword(
            Guid userId, AdminResetPasswordRequest request, CancellationToken ct = default)
        {
            try
            {
                await _authService.AdminResetPasswordAsync(userId, request.NewPassword, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
