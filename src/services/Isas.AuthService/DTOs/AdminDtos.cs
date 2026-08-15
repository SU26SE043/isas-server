using System.ComponentModel.DataAnnotations;

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

        /// <summary>F20 — thời điểm bị PlatformAdmin đình chỉ (null = đang hoạt động).</summary>
        public DateTime? BannedAt { get; set; }

        /// <summary>F20 — lý do đình chỉ (chỉ hiển thị cho admin).</summary>
        public string? BanReason { get; set; }
    }

    /// <summary>F20 — POST /auth/admin/users/{id}/ban. Lý do tuỳ chọn nhưng nên có (để đối chất).</summary>
    public class BanUserRequest
    {
        [MaxLength(500)]
        public string? Reason { get; set; }
    }

    /// <summary>
    /// F20 — POST /auth/admin/users/{id}/reset-password. Mật khẩu mới do admin đặt; vẫn phải qua
    /// validator mật khẩu của Identity (yếu → 400).
    /// </summary>
    public class AdminResetPasswordRequest
    {
        [Required]
        public string NewPassword { get; set; } = null!;
    }

    /// <summary>
    /// POST /auth/admin/users/{id}/role — đổi platform-role (AUTH-3).
    ///
    /// ⚠ <see cref="Role"/> KHÔNG validate bằng "role này có tồn tại trong bảng roles không": role là
    /// string tự do, được tạo LAZILY (<c>EnsureRoleExistsAsync</c>) nên một cái tên gõ sai vừa lọt
    /// kiểm tra vừa đẻ thêm một role rác. Chỉ 3 tên trong AUTH-3 được chấp nhận — xem
    /// <c>AuthService.PlatformRoles</c>.
    /// </summary>
    public class ChangePlatformRoleRequest
    {
        /// <summary>Một trong: <c>Candidate</c> | <c>Employer</c> | <c>Admin</c>. Phân biệt hoa thường.</summary>
        [Required]
        public string Role { get; set; } = null!;
    }
}
