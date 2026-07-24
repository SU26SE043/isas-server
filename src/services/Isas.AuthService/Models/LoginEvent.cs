namespace Isas.AuthService.Models
{
    /// <summary>
    /// FR18 — đếm lượt đăng nhập, append-only, KHÔNG sổ sách (mẫu <c>ai_usage_logs</c> F22).
    /// KHÔNG lưu IP/user-agent: analytics chỉ cần đếm, lưu thêm là mở bề mặt PII không ai yêu cầu.
    /// </summary>
    public class LoginEvent
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }

        /// <summary>Password | Google | MagicLink — xem <see cref="LoginMethod"/>.</summary>
        public string Method { get; set; } = default!;

        public DateTime CreatedAt { get; set; }
    }

    public static class LoginMethod
    {
        public const string Password = "Password";
        public const string Google = "Google";
        public const string MagicLink = "MagicLink";
    }
}
