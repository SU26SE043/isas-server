using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.AuthService.Controllers
{
    /// <summary>
    /// A6 (AUTH-4/AUTH-8) — OrgAdmin quản thành viên org: mời/tạo <c>HrMember</c> + xem danh sách.
    /// Chỉ OrgAdmin thao tác (đọc claim <c>org_role</c> OFFLINE — GEN-3); HrMember/Candidate/không claim → 403.
    /// Billing/campaign gắn theo ORG (AUTH-8): thành viên tạo ở đây thuộc đúng org của caller.
    /// </summary>
    [ApiController]
    [Route("auth/org/members")]
    [Authorize(Roles = "Employer")] // A5 — quản thành viên org = Employer; IsOrgAdmin() siết tiếp OrgAdmin trong code.
    public class OrgMembersController : ControllerBase
    {
        private readonly IAuthService _authService;

        public OrgMembersController(IAuthService authService)
        {
            _authService = authService;
        }

        // POST /auth/org/members — OrgAdmin tạo HrMember (passwordless; HR đặt mật khẩu qua forgot/reset).
        // Email đã có account → 409. → 201 + member info.
        [HttpPost]
        public async Task<ActionResult<OrgMemberResponse>> AddMember(
            AddOrgMemberRequest request, CancellationToken ct = default)
        {
            if (!User.IsOrgAdmin())
                return Forbid();

            var orgId = User.GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                var member = await _authService.AddOrgMemberAsync(orgId.Value, request.Email, request.FullName, ct);
                return CreatedAtAction(nameof(ListMembers), null, member);
            }
            catch (OrgMemberConflictException ex)
            {
                return Conflict(new { error = ex.Message });
            }
        }

        // GET /auth/org/members — OrgAdmin liệt kê thành viên org của mình.
        [HttpGet]
        public async Task<ActionResult<IReadOnlyList<OrgMemberResponse>>> ListMembers(CancellationToken ct = default)
        {
            if (!User.IsOrgAdmin())
                return Forbid();

            var orgId = User.GetOrgId();
            if (orgId is null)
                return Forbid();

            var members = await _authService.ListOrgMembersAsync(orgId.Value, ct);
            return Ok(members);
        }

        // PATCH /auth/org/members/{userId} — OrgAdmin đổi org_role thành viên (OrgAdmin↔HrMember).
        // role sai → 400; không thuộc org → 404; hạ cấp OrgAdmin cuối → 409.
        [HttpPatch("{userId:guid}")]
        public async Task<ActionResult<OrgMemberResponse>> ChangeRole(
            Guid userId, ChangeOrgMemberRoleRequest request, CancellationToken ct = default)
        {
            if (!User.IsOrgAdmin())
                return Forbid();

            var orgId = User.GetOrgId();
            if (orgId is null)
                return Forbid();

            if (!Enum.TryParse<OrgRole>(request.OrgRole, ignoreCase: false, out var newRole)
                || !Enum.IsDefined(newRole))
                return BadRequest(new { error = "orgRole must be 'OrgAdmin' or 'HrMember'" });

            try
            {
                var member = await _authService.ChangeOrgMemberRoleAsync(orgId.Value, userId, newRole, ct);
                return Ok(member);
            }
            catch (OrgMemberNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (OrgMemberConflictException ex)
            {
                return Conflict(new { error = ex.Message });
            }
        }

        // DELETE /auth/org/members/{userId} — OrgAdmin xoá thành viên khỏi org (hard-remove membership).
        // tự xoá mình → 400; không thuộc org → 404; xoá OrgAdmin cuối → 409; xong → 204.
        [HttpDelete("{userId:guid}")]
        public async Task<IActionResult> RemoveMember(Guid userId, CancellationToken ct = default)
        {
            if (!User.IsOrgAdmin())
                return Forbid();

            var orgId = User.GetOrgId();
            if (orgId is null)
                return Forbid();

            var actingUserId = User.GetUserId();
            if (actingUserId is null)
                return Forbid();

            if (actingUserId.Value == userId)
                return BadRequest(new { error = "Cannot remove yourself from the organization" });

            try
            {
                await _authService.RemoveOrgMemberAsync(orgId.Value, userId, ct);
                return NoContent();
            }
            catch (OrgMemberNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (OrgMemberConflictException ex)
            {
                return Conflict(new { error = ex.Message });
            }
        }
    }
}
