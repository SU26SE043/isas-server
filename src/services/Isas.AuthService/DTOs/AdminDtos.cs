namespace Isas.AuthService.DTOs
{
    /// <summary>
    /// PlatformAdmin oversight (AUTH-7) — GET /auth/admin/users. Một dòng gộp thông tin định danh
    /// (User) + platform-role (Identity) + membership org (OrgMember ⊕ Organization) nếu có.
    /// Read-only, cross-org (khác OrgMemberResponse chỉ trong 1 org).
    /// </summary>
    public class AdminUserResponse
    {
        public Guid Id { get; set; }
        public string? Email { get; set; }
        public string? FullName { get; set; }

        /// <summary>Platform-role (Candidate / Employer / Admin) — lấy qua UserManager.GetRolesAsync.</summary>
        public string Role { get; set; } = null!;

        /// <summary>Org user thuộc về (null = không thuộc org nào, vd Candidate B2C).</summary>
        public Guid? OrgId { get; set; }
        public string? OrgName { get; set; }

        /// <summary>Org-role string: <c>OrgAdmin</c> | <c>HrMember</c> (null nếu không thuộc org).</summary>
        public string? OrgRole { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
