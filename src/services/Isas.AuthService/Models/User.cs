using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Models
{
    public class User : IdentityUser<Guid>
    {
        public string? FullName { get; set; }

        public string? Location { get; set; }

        public string? Title { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// F20 (FR16) — mốc PlatformAdmin ĐÌNH CHỈ account (null = đang hoạt động).
        ///
        /// ⚠ KHÔNG dùng lại <c>LockoutEnd</c> của Identity cho việc này: cột đó là khoá TỰ ĐỘNG do
        /// nhập sai mật khẩu (<c>CheckPasswordSignInAsync(lockoutOnFailure: true)</c>), Identity tự
        /// đặt/xoá nó — gộp hai thứ vào một cột thì (a) không phân biệt được "bị admin cấm" với "gõ
        /// sai mật khẩu 5 lần", và (b) một lần đăng nhập thành công / reset mật khẩu sẽ vô tình GỠ
        /// lệnh cấm của admin. Ban là quyết định của con người, phải có cột riêng + lý do + người ra
        /// quyết định.
        /// </summary>
        public DateTime? BannedAt { get; set; }

        /// <summary>Lý do đình chỉ (admin nhập, hiển thị lại cho admin khác — không gửi cho user).</summary>
        public string? BanReason { get; set; }

        /// <summary>Admin đã ra quyết định đình chỉ (ref lỏng tới users.id — phục vụ đối chất).</summary>
        public Guid? BannedBy { get; set; }

        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

        public ICollection<UserClaim> UserClaims { get; set; } = new List<UserClaim>();

        public ICollection<UserToken> UserTokens { get; set; } = new List<UserToken>();

        public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

        public ICollection<OrgMember> OrgMembers { get; set; } = new List<OrgMember>();
    }
}
