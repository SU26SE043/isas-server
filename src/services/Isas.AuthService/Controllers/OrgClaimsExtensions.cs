using System.Security.Claims;

namespace Isas.AuthService.Controllers
{
    /// <summary>
    /// A6 (AUTH-4/AUTH-5/AUTH-8) — đọc org-context OFFLINE từ JWT claim (GEN-3, không gọi lại chính mình).
    /// Quản thành viên org = quyền <c>OrgAdmin</c>; claim do JwtService phát:
    /// <c>org_id</c> (Guid) + <c>org_role</c> (<c>OrgAdmin</c>|<c>HrMember</c>) khi user thuộc org.
    /// </summary>
    internal static class OrgClaimsExtensions
    {
        public const string OrgIdClaim = "org_id";
        public const string OrgRoleClaim = "org_role";
        public const string OrgAdminRole = "OrgAdmin";

        /// <summary>True nếu caller là OrgAdmin (có claim <c>org_role=OrgAdmin</c>).</summary>
        public static bool IsOrgAdmin(this ClaimsPrincipal user) =>
            user.HasClaim(c => c.Type == OrgRoleClaim && c.Value == OrgAdminRole);

        /// <summary>org_id của caller (null nếu thiếu/không parse được → không thuộc org).</summary>
        public static Guid? GetOrgId(this ClaimsPrincipal user) =>
            Guid.TryParse(user.FindFirstValue(OrgIdClaim), out var orgId) ? orgId : null;
    }
}
