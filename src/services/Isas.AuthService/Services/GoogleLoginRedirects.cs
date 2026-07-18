using System.Text;

namespace Isas.AuthService.Services
{
    /// <summary>
    /// Dựng mọi URL của vòng đăng nhập Google.
    /// <para>
    /// Nguyên tắc bảo mật: đích redirect LUÔN lấy từ <b>config server</b>
    /// (<c>Frontend:BaseUrl</c> / <c>Gateway:PublicBaseUrl</c>), TUYỆT ĐỐI không lấy host do client
    /// truyền vào — redirect tới host client chỉ định là lỗ open-redirect và sẽ tuồn access/refresh
    /// token cho site của kẻ tấn công. <c>returnUrl</c> (nếu có) chỉ được chấp nhận dưới dạng
    /// <b>đường dẫn tương đối</b> rồi ghép sau base URL đã cấu hình.
    /// </para>
    /// </summary>
    public interface IGoogleLoginRedirects
    {
        /// <summary>URL tuyệt đối (public, qua gateway) của action callback MVC — nơi handler Google trả về sau khi xong.</summary>
        string CallbackUrl(string? returnUrl);

        /// <summary>
        /// URL FE kèm <b>mã dùng-một-lần</b> (<c>?code=…</c>) — KHÔNG kèm token.
        /// </summary>
        string SuccessUrl(string code, string? returnUrl);

        /// <summary>URL FE kèm <c>?error=&lt;mã&gt;</c> (đăng nhập thất bại).</summary>
        string FailureUrl(string errorCode);
    }

    public sealed class GoogleLoginRedirects : IGoogleLoginRedirects
    {
        /// <summary>Route (nội bộ) của action MVC callback — public qua gateway là <c>{PublicBaseUrl}/auth/login-google-callback</c>.</summary>
        public const string BackendCallbackPath = "/auth/login-google-callback";

        /// <summary>
        /// CallbackPath của <b>handler</b> Google. PHẢI khác <see cref="BackendCallbackPath"/>:
        /// middleware remote-auth chạy TRƯỚC MVC và short-circuit đúng path nó giữ — trùng nhau thì
        /// action MVC không bao giờ được gọi (bug cũ).
        /// </summary>
        public const string OAuthCallbackPath = "/auth/signin-google";

        /// <summary>Route trang callback bên Angular.</summary>
        public const string FrontendCallbackPath = "/auth/google/callback";

        /// <summary>Path (nội bộ) của action khởi tạo challenge — cần ép origin công khai giống OAuth callback.</summary>
        public const string LoginPath = "/auth/login-google";

        private readonly string? _frontendBaseUrl;
        private readonly string? _publicBaseUrl;

        public GoogleLoginRedirects(IConfiguration configuration)
        {
            // Không throw trong ctor: service này được inject vào AuthController (dùng cho MỌI request auth).
            // Thiếu config chỉ được phép làm hỏng 2 endpoint Google, không phải cả service.
            _frontendBaseUrl = configuration["Frontend:BaseUrl"];
            _publicBaseUrl = configuration["Gateway:PublicBaseUrl"];
        }

        public string CallbackUrl(string? returnUrl)
        {
            var url = Require(_publicBaseUrl, "Gateway:PublicBaseUrl") + BackendCallbackPath;
            var safe = SanitizeReturnUrl(returnUrl);
            return safe is null ? url : $"{url}?returnUrl={Uri.EscapeDataString(safe)}";
        }

        public string SuccessUrl(string code, string? returnUrl)
        {
            // URL này KHÔNG mang token, chỉ mang mã tham chiếu ngắn hạn dùng-một-lần. Trước đây token
            // đi trong fragment — kín với server log/Referer, nhưng vẫn nằm trong location.hash nên
            // script cùng trang (kể cả extension trình duyệt độc hại) đọc được. Mã thì đọc trộm cũng
            // vô dụng nếu FE đã đổi trước: đổi lần hai là thất bại.
            var query = new StringBuilder("?code=").Append(Uri.EscapeDataString(code));

            var safe = SanitizeReturnUrl(returnUrl);
            if (safe is not null)
                query.Append("&returnUrl=").Append(Uri.EscapeDataString(safe));

            return FrontendCallbackBase() + query;
        }

        public string FailureUrl(string errorCode) =>
            FrontendCallbackBase() + "?error=" + Uri.EscapeDataString(errorCode);

        private string FrontendCallbackBase() =>
            Require(_frontendBaseUrl, "Frontend:BaseUrl") + FrontendCallbackPath;

        private static string Require(string? value, string key) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new InvalidOperationException($"{key} is missing in configuration.")
                : value.TrimEnd('/');

        /// <summary>
        /// Lọc <c>returnUrl</c> do client truyền: CHỈ chấp nhận đường dẫn tương đối trong app FE
        /// (vd <c>/candidate/dashboard</c>). Loại mọi dạng có thể trỏ ra ngoài — URL tuyệt đối,
        /// <c>//evil.com</c> và <c>/\evil.com</c> (trình duyệt hiểu như protocol-relative), chuỗi có
        /// scheme (<c>:</c>), ký tự điều khiển (CR/LF → header injection). Không hợp lệ → <c>null</c>
        /// (bên gọi dùng đích mặc định), KHÔNG bao giờ dùng nguyên si.
        /// </summary>
        public static string? SanitizeReturnUrl(string? returnUrl)
        {
            if (string.IsNullOrWhiteSpace(returnUrl)) return null;

            var value = returnUrl.Trim();
            if (value.Length > 512) return null;
            if (value[0] != '/') return null;
            if (value.Length > 1 && (value[1] == '/' || value[1] == '\\')) return null;
            if (value.Contains(':')) return null;
            if (value.Any(char.IsControl)) return null;

            return value;
        }
    }
}
