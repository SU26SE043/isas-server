using System.Security.Claims;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// A4 (AUTH-4/AUTH-6) — <b>HrMember không có quyền billing</b>: các endpoint money-mutation (mua pack,
    /// tất toán hóa đơn, chốt kỳ) chỉ dành cho OrgAdmin (B2B) hoặc User cá nhân (B2C). Kiểm tra OFFLINE bằng
    /// claim <c>org_role</c> trong JWT (GEN-3, không gọi AuthService). Chặn CHỈ khi <c>org_role=HrMember</c>:
    /// B2C không mang claim <c>org_role</c> và OrgAdmin đều KHÔNG bị chặn.
    /// </summary>
    internal static class BillingAuthorizationExtensions
    {
        /// <summary>Claim type org-role do AuthService phát (JwtService: <c>new Claim("org_role", ...)</c>).</summary>
        public const string OrgRoleClaim = "org_role";

        /// <summary>Giá trị org-role bị chặn billing (<c>OrgRole.HrMember.ToString()</c>).</summary>
        public const string HrMemberRole = "HrMember";

        public static bool IsHrMember(this ClaimsPrincipal user) =>
            user.HasClaim(c => c.Type == OrgRoleClaim && c.Value == HrMemberRole);
    }
}
