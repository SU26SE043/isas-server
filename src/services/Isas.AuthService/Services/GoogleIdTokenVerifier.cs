using Google.Apis.Auth;

namespace Isas.AuthService.Services
{
    /// <summary>
    /// Phần thông tin ISAS cần từ một Google ID token <b>đã verify</b> (chữ ký + <c>iss</c> + <c>exp</c>
    /// + <c>aud</c> đều đã qua). Cố ý hẹp: chỉ khai thứ luồng đăng nhập dùng tới, không bê nguyên
    /// payload của Google vào tầng nghiệp vụ.
    /// </summary>
    /// <param name="Subject">
    /// Claim <c>sub</c> của Google — định danh BỀN VỮNG của account Google, không đổi khi người dùng
    /// đổi email. Đây là giá trị PHẢI dùng làm <c>ProviderKey</c>: xem ghi chú ở
    /// <see cref="AuthService.LoginGoogleWithIdTokenAsync"/>.
    /// </param>
    public sealed record GoogleIdTokenPayload(string Subject, string? Email, bool EmailVerified, string? Name);

    /// <summary>
    /// Token do client gửi lên không dùng được: sai chữ ký, hết hạn, <c>aud</c> ngoài allowlist, hoặc
    /// thiếu/chưa xác minh email. Cố ý KHÔNG chia nhỏ theo lý do — bên ngoài chỉ thấy một câu trả lời
    /// duy nhất (401), phân biệt ra ngoài chỉ giúp người dò biết mình sai ở đâu.
    /// </summary>
    public sealed class InvalidGoogleIdTokenException(string message) : Exception(message);

    /// <summary>
    /// Verify Google ID token (đăng nhập native trên mobile). Tách interface để test không phải gọi
    /// Google thật — cùng mẫu <c>IPayOsQueryClient</c> bọc SDK PayOS (P3).
    /// </summary>
    public interface IGoogleIdTokenVerifier
    {
        /// <exception cref="InvalidGoogleIdTokenException">Token không hợp lệ.</exception>
        /// <exception cref="InvalidOperationException">Server chưa cấu hình allowlist <c>aud</c>.</exception>
        Task<GoogleIdTokenPayload> VerifyAsync(string idToken, CancellationToken ct = default);
    }

    public sealed class GoogleIdTokenVerifier : IGoogleIdTokenVerifier
    {
        public const string AudiencesKey = "Authentication:Google:IdTokenAudiences";
        public const string ClientIdKey = "Authentication:Google:ClientId";

        private readonly IConfiguration _configuration;
        private readonly ILogger<GoogleIdTokenVerifier> _logger;

        public GoogleIdTokenVerifier(IConfiguration configuration, ILogger<GoogleIdTokenVerifier> logger)
        {
            // Không throw trong ctor dù config có thiếu (mẫu GoogleLoginRedirects): service này nằm
            // trong đường DI của AuthController — thiếu config chỉ được phép làm hỏng ĐÚNG endpoint
            // đăng nhập bằng ID token, không phải cả AuthService.
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Danh sách <c>aud</c> được chấp nhận. Trống thì rơi về <c>Authentication:Google:ClientId</c>
        /// (web client) — vì trên Android/iOS, ID token xin kèm <c>serverClientId</c> mang <c>aud</c>
        /// chính là web client ID, nên cấu hình mặc định đã đúng cho đa số trường hợp.
        /// <para>
        /// 🔴 Cả hai đều trống thì <b>NÉM</b>, tuyệt đối không trả danh sách rỗng:
        /// <c>ValidationSettings.Audience</c> để null/rỗng nghĩa là <b>BỎ QUA kiểm tra aud</b> —
        /// lúc đó BẤT KỲ Google ID token nào trên đời (token của app khác, token kẻ tấn công tự lấy
        /// từ project Google của họ) cũng đăng nhập được vào ISAS. Fail-closed: thà 500 còn hơn mở toang.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> ResolveAudiences(IConfiguration configuration)
        {
            var configured = configuration.GetSection(AudiencesKey).Get<string[]>() ?? [];

            var audiences = configured
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (audiences.Count == 0)
            {
                var clientId = configuration[ClientIdKey];
                if (!string.IsNullOrWhiteSpace(clientId))
                    audiences.Add(clientId.Trim());
            }

            return audiences.Count > 0
                ? audiences
                : throw new InvalidOperationException(
                    $"{AudiencesKey} (và {ClientIdKey}) đều trống — không thể verify Google ID token. " +
                    "Bỏ trống sẽ khiến mọi Google ID token đăng nhập được, nên đường này từ chối chạy.");
        }

        public async Task<GoogleIdTokenPayload> VerifyAsync(string idToken, CancellationToken ct = default)
        {
            // Ném InvalidGoogleIdTokenException (→401) chứ không để rỗng đi tiếp: token rỗng là lỗi
            // của client, không phải lỗi cấu hình server.
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidGoogleIdTokenException("ID token rỗng");

            // Đọc config mỗi lần gọi (không cache trong ctor): tần suất thấp (1 lần/đăng nhập) mà đổi
            // được allowlist bằng restart là đủ, đổi lại tránh ném ngay lúc khởi động khi thiếu config.
            var audiences = ResolveAudiences(_configuration);

            // ValidateAsync tự kiểm CHỮ KÝ (khoá công khai Google, có cache), iss, exp và aud.
            // KHÔNG tự kiểm lại mấy thứ đó ở đây: "kiểm hai nơi = một nơi luôn sai".
            var settings = new GoogleJsonWebSignature.ValidationSettings { Audience = audiences };

            GoogleJsonWebSignature.Payload payload;
            try
            {
                payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
            }
            catch (InvalidJwtException ex)
            {
                // Log allowlist ĐANG cấu hình (client ID không phải bí mật) — lỗi hay gặp nhất của
                // đường này là app mobile đặt serverClientId sai, và triệu chứng chỉ là 401 câm.
                // KHÔNG log idToken: nó thay mặt cho cả một phiên đăng nhập.
                _logger.LogWarning(
                    "Google ID token không hợp lệ: {Reason}. Server đang chấp nhận aud: {Audiences}",
                    ex.Message, string.Join(", ", audiences));

                throw new InvalidGoogleIdTokenException("Google ID token không hợp lệ hoặc đã hết hạn");
            }

            return new GoogleIdTokenPayload(
                payload.Subject,
                payload.Email,
                payload.EmailVerified is true,
                payload.Name);
        }
    }
}
