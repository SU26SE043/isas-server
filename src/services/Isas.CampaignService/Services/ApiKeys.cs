using System.Security.Cryptography;
using System.Text;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// F17 — máy sinh + băm API key cho bên thứ ba. Cùng lược đồ với <see cref="InvitationTokens"/>
    /// (DB23) và refresh token AuthService (DB12): SHA-256 một chiều, DB chỉ giữ dấu vân tay.
    /// </summary>
    public static class ApiKeys
    {
        /// <summary>
        /// Tiền tố nhận dạng. Có ích thật chứ không phải trang trí: chuỗi cố định cho phép secret-scanner
        /// (GitHub push protection, gitleaks) bắt được key ISAS bị commit nhầm vào repo khách hàng.
        /// </summary>
        public const string Prefix = "isas_ak_";

        /// <summary>Số ký tự đầu phần ngẫu nhiên lưu lại để hiển thị (KHÔNG dùng xác thực).</summary>
        public const int DisplayPrefixLength = 6;

        /// <summary>
        /// Key thô: <c>isas_ak_</c> + 32 byte CSPRNG mã base64url (43 ký tự, 256 bit).
        /// Chỉ trả về ĐÚNG MỘT LẦN lúc tạo — sau đó không đường nào đọc lại (đó là điểm của việc băm).
        /// </summary>
        public static string NewRawKey()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            var random = Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
            return Prefix + random;
        }

        /// <summary>SHA-256(UTF8(key)) → base64 44 ký tự. Deterministic → tra cứu là index probe.</summary>
        public static string Hash(string rawKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawKey);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Tiền tố hiển thị (6 ký tự đầu SAU <c>isas_ak_</c>). Key không đúng định dạng → chuỗi rỗng.
        /// </summary>
        public static string DisplayPrefix(string rawKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawKey);
            if (!rawKey.StartsWith(Prefix, StringComparison.Ordinal)) return string.Empty;
            var random = rawKey[Prefix.Length..];
            return random.Length <= DisplayPrefixLength ? random : random[..DisplayPrefixLength];
        }
    }
}
