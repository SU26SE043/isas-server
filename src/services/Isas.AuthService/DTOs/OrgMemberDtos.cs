using System.ComponentModel.DataAnnotations;

namespace Isas.AuthService.DTOs
{
    /// <summary>
    /// A6 (AUTH-4/AUTH-8) — OrgAdmin mời/tạo thành viên <c>HrMember</c> vào org của mình.
    /// HR sẽ đăng nhập bằng email + mật khẩu tự đặt qua luồng forgot/reset (tạo passwordless,
    /// giống ProvisionCandidate) — OrgAdmin KHÔNG đặt hộ mật khẩu người khác.
    /// </summary>
    public class AddOrgMemberRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = null!;

        [Required]
        public string FullName { get; set; } = null!;
    }

    /// <summary>Thông tin thành viên org trả về cho OrgAdmin (create + list).</summary>
    public class OrgMemberResponse
    {
        public Guid UserId { get; set; }
        public string Email { get; set; } = null!;
        public string? FullName { get; set; }

        /// <summary>Org-role lưu string: <c>OrgAdmin</c> | <c>HrMember</c>.</summary>
        public string OrgRole { get; set; } = null!;

        /// <summary>Xấp xỉ thời điểm tham gia = thời điểm tạo account (org_members chưa có cột riêng — phase 1).</summary>
        public DateTime JoinedAt { get; set; }
    }
}
