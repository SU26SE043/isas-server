using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.AuthService.Controllers
{
    /// <summary>
    /// Thông tin tổ chức của caller (Employer). <c>GET</c> — mọi member org đọc; <c>PUT</c> — chỉ OrgAdmin
    /// (mirror <see cref="OrgMembersController"/>). Route <c>auth/org</c> KHÔNG đụng <c>auth/org/members</c>.
    /// org_id lấy OFFLINE từ JWT (GEN-3). Không có migration — sửa cột name/tax_code có sẵn.
    /// </summary>
    [ApiController]
    [Route("auth/org")]
    [Authorize(Roles = "Employer")]
    public class OrganizationController : ControllerBase
    {
        private readonly IAuthService _authService;

        public OrganizationController(IAuthService authService)
        {
            _authService = authService;
        }

        // GET /auth/org — thông tin tổ chức (mọi member đọc được).
        [HttpGet]
        public async Task<ActionResult<OrganizationResponse>> GetOrg(CancellationToken ct = default)
        {
            var orgId = User.GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                return Ok(await _authService.GetOrganizationAsync(orgId.Value, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        // PUT /auth/org — sửa name/taxCode. Chỉ OrgAdmin (HrMember → 403).
        [HttpPut]
        public async Task<ActionResult<OrganizationResponse>> UpdateOrg(
            UpdateOrgRequest request, CancellationToken ct = default)
        {
            if (!User.IsOrgAdmin())
                return Forbid();

            var orgId = User.GetOrgId();
            if (orgId is null)
                return Forbid();

            try
            {
                return Ok(await _authService.UpdateOrganizationAsync(orgId.Value, request, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }
    }
}
