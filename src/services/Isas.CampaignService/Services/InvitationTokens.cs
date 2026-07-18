using System.Security.Cryptography;
using System.Text;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// DB23 — máy sinh + băm token magic-link mời ứng viên.
    ///
    /// VÌ SAO băm: token thô redeem được thẳng (mở link → provision account Candidate → trả JWT),
    /// nên lưu thô vào <c>campaign_invitations.token</c> nghĩa là ai đọc được DB/backup là mạo danh
    /// được MỌI invitee đang mở. Băm SHA-256 một chiều → DB chỉ giữ "dấu vân tay"; token thô chỉ tồn
    /// tại trong email/URL gửi cho ứng viên.
    ///
    /// Cùng lược đồ với refresh token của AuthService (<c>JwtService.HashRefreshToken</c>):
    /// SHA-256(UTF8(token)) → base64. KHÔNG cần salt/KDF vì token là 256-bit ngẫu nhiên CSPRNG
    /// (không đoán được bằng brute-force/rainbow-table như mật khẩu người dùng).
    /// </summary>
    public static class InvitationTokens
    {
        /// <summary>Token magic-link 1 lần — 256-bit random, URL-safe base64 (không padding).</summary>
        public static string NewRawToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
        }

        /// <summary>
        /// SHA-256(UTF8(token)) → base64 **44 ký tự** (32 byte hash) — vừa thoải mái cột
        /// <c>varchar(128)</c> sẵn có nên KHÔNG cần đổi kiểu/độ dài cột.
        /// Deterministic → tra cứu vẫn là single-row probe trên UNIQUE index.
        /// </summary>
        public static string Hash(string rawToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
            return Convert.ToBase64String(hash);
        }
    }
}
