using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using static Isas.AuthService.DTOs.ForgotPasswordDtos;

namespace Isas.AuthService.Controllers
{
    [Route("auth")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly UserManager<User> _userManager;
        private readonly SignInManager<User> _signInManager;
        private readonly IEmailSender _emailSender;
        private readonly IGoogleLoginRedirects _googleRedirects;
        private readonly IGoogleAuthCodeStore _googleCodes;
        private readonly ILogger<AuthController> _logger;
        public AuthController(IAuthService authService, UserManager<User> userManager, SignInManager<User> signInManager, IEmailSender emailSender,
            IGoogleLoginRedirects googleRedirects, IGoogleAuthCodeStore googleCodes, ILogger<AuthController> logger)
        {
            _authService = authService;
            _userManager = userManager;
            _signInManager = signInManager;
            _emailSender = emailSender;
            _googleRedirects = googleRedirects;
            _googleCodes = googleCodes;
            _logger = logger;
        }

        // A5 — auth-entry công khai (chưa có JWT): AllowAnonymous tường minh (rõ ý định + phòng tương lai).
        [AllowAnonymous]
        [HttpPost("register")]
        public async Task<ActionResult<AuthResponse>> RegisterAsync(RegisterRequest registerRequest)
        {
            var existingUser = await _userManager.FindByEmailAsync(registerRequest.Email);

            // 409 (không phải 400): "tài nguyên đã tồn tại" là XUNG ĐỘT trạng thái, không phải đầu vào
            // sai dạng. Thống nhất với POST /auth/org/members vốn đã trả 409 cho cùng tình huống —
            // trước đây hai đường trùng-email trả hai mã khác nhau. Body dạng { error } để FE rút được
            // message hiển thị (extractErrorMessage đọc key `error`); chuỗi trần thì FE nuốt mất.
            if (existingUser != null)
            {
                return Conflict(new { error = "Email already exists" });
            }

            var result = await _authService.RegisterAsync(registerRequest);

            return Ok(result);
        }

        [AllowAnonymous]
        [HttpPost("register-org")]
        public async Task<ActionResult<AuthResponse>> RegisterOrgAsync(RegisterOrgRequest request)
        {
            var existingUser = await _userManager.FindByEmailAsync(request.Email);
            if (existingUser != null)
                return Conflict(new { error = "Email already exists" });   // 409 — xem ghi chú ở register

            var result = await _authService.RegisterOrgAsync(request);
            return Ok(result);
        }

        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginRequest request)
        {
            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return Unauthorized("Invalid credentials");

            var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

            if (result.IsLockedOut) return Unauthorized("Account locked");
            if (!result.Succeeded) return Unauthorized("Invalid credentials");

            try
            {
                return Ok(await _authService.LoginAsync(request));
            }
            catch (UserBannedException ex)
            {
                // 403 chứ không 401: thông tin đăng nhập ĐÚNG (đã qua CheckPasswordSignInAsync ở trên),
                // cái bị từ chối là quyền dùng hệ thống. 401 sẽ khiến FE mời người dùng thử lại mật
                // khẩu — họ gõ đúng rồi, thử mãi cũng không vào được. (F20)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
            }
        }

        // OAuth Google là điều hướng CẢ TRANG, không phải XHR: người dùng rời app Angular sang
        // accounts.google.com rồi quay lại. Vì vậy redirect-uri phải là URL TUYỆT ĐỐI công khai
        // (qua gateway) — handler redirect verbatim, trình duyệt phải với tới được.
        // (Bug cũ: Url.Action(..., "Account", ...) trỏ tới controller KHÔNG tồn tại trong service này
        // → trả null → RedirectUri không được set.)
        [AllowAnonymous]
        [HttpGet("login-google")]
        public IActionResult LoginWithGoogle(string? returnUrl = null)
        {
            var redirectUrl = _googleRedirects.CallbackUrl(returnUrl);
            var properties = _signInManager.ConfigureExternalAuthenticationProperties("Google", redirectUrl);

            return Challenge(properties, "Google");
        }

        // Đích cuối của vòng OAuth. KHÔNG trả JSON (bug cũ): người dùng đang ở một điều hướng cả
        // trang nên sẽ đáp xuống trang JSON thô và app Angular không bao giờ chạy lại để nhận token.
        // Thay vào đó 302 về FE — nhưng CHỈ kèm mã dùng-một-lần, KHÔNG kèm token: token trong URL
        // (kể cả ở fragment) vẫn đọc được từ phía trình duyệt (location.hash, extension). FE đổi mã
        // lấy phiên qua POST /auth/google/exchange. Đích lấy từ config server, không nhận host từ client.
        [AllowAnonymous]
        [HttpGet("login-google-callback")]
        public async Task<IActionResult> GoogleLoginCallback(string? returnUrl = null, string? remoteError = null)
        {
            if (remoteError != null)
            {
                _logger.LogWarning("Google trả lỗi khi đăng nhập: {RemoteError}", remoteError);
                return Redirect(_googleRedirects.FailureUrl("remote_error"));
            }

            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                // Thường là cookie external hết hạn / bị chặn, hoặc user mở thẳng URL này.
                _logger.LogWarning("Không đọc được ExternalLoginInfo ở callback Google");
                return Redirect(_googleRedirects.FailureUrl("no_login_info"));
            }

            AuthResponse authResponse;
            try
            {
                authResponse = await _authService.LoginGoogleAsync(info);
            }
            catch (UserBannedException)
            {
                // F20 — account bị đình chỉ cũng phải chặn ở đường Google, và trả mã riêng để FE nói
                // đúng lý do (rơi vào "login_failed" chung thì người dùng cứ thử lại vô ích).
                _logger.LogWarning("Từ chối đăng nhập Google: account đã bị đình chỉ");
                return Redirect(_googleRedirects.FailureUrl("account_suspended"));
            }
            catch (Exception ex)
            {
                // Không để lộ chi tiết lỗi ra URL — chỉ mã lỗi cho FE hiển thị, chi tiết vào log.
                _logger.LogError(ex, "Đăng nhập Google thất bại khi phát hành phiên");
                return Redirect(_googleRedirects.FailureUrl("login_failed"));
            }

            // Cookie external chỉ phục vụ 1 vòng OAuth — dọn ngay để không còn dấu vết phiên.
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            // Phiên nằm lại server; ra URL chỉ là mã tham chiếu ngắn hạn. KHÔNG log giá trị mã —
            // log là một bản sao vĩnh viễn của thứ đang thay mặt cho cả access + refresh token.
            var code = _googleCodes.Issue(authResponse);
            return Redirect(_googleRedirects.SuccessUrl(code, returnUrl));
        }

        // Chặng 2 của đăng nhập Google: FE gửi mã vừa nhận qua redirect, đổi lấy phiên thật.
        // AllowAnonymous vì đúng lúc này người dùng CHƯA có token — mã chính là bằng chứng đã qua
        // được vòng OAuth. Sai/hết hạn/đã dùng đều trả 400 với CÙNG một thông điệp: phân biệt ra
        // ngoài chỉ giúp kẻ dò mã biết mình đoán gần đúng.
        [AllowAnonymous]
        [HttpPost("google/exchange")]
        public ActionResult<AuthResponse> ExchangeGoogleCode(GoogleExchangeRequest request)
        {
            var auth = _googleCodes.Consume(request.Code);
            if (auth is null)
            {
                _logger.LogWarning("Đổi mã đăng nhập Google thất bại (mã sai, hết hạn hoặc đã dùng)");
                return BadRequest("Mã đăng nhập không hợp lệ hoặc đã hết hạn");
            }

            return Ok(auth);
        }

        // Tính hợp lệ của refresh token do AuthService quyết định MỘT CHỖ DUY NHẤT: trước đây controller
        // tự tiền-kiểm `IsRevoked` rồi mới gọi service, nên token vừa bị xoay vòng chết ở đây và không
        // bao giờ tới được cửa sổ ân hạn (đua refresh nhiều tab). Kiểm hai nơi = một nơi luôn sai.
        [AllowAnonymous]
        [HttpPost("refresh")]
        public async Task<ActionResult<RefreshTokenResponse>> RefreshTokenAsync(RefreshTokenRequest refreshTokenRequest)
        {
            try
            {
                var result = await _authService.RefreshTokenAsync(refreshTokenRequest.RefreshToken);
                return Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                // Không tiết lộ token sai vì lý do gì (không tồn tại / hết hạn / quá cửa sổ ân hạn).
                return Unauthorized("Refresh token expired or revoked");
            }
            catch (UserBannedException)
            {
                // F20 — account bị đình chỉ. Trả 401 (không phải 403) để giữ nguyên hợp đồng của
                // đường refresh: FE đã xử lý 401 = hết phiên → về trang đăng nhập, và ở ĐÓ họ nhận
                // 403 kèm lý do thật. Đường này gần như không tới được (ban đã thu hồi token).
                return Unauthorized("Refresh token expired or revoked");
            }
        }

        // Đăng xuất theo USER đang đăng nhập (claim `sub`), không theo riêng token gửi kèm: thu hồi mọi
        // refresh token → tab khác không gia hạn phiên tiếp được. Body vẫn nhận `refreshToken` để giữ
        // hợp đồng cũ với FE, nhưng KHÔNG còn quyết định phạm vi thu hồi.
        [Authorize(Roles = "Candidate, Employer, Admin")]
        [HttpPost("logout")]
        public async Task<IActionResult> LogoutAsync(RefreshTokenRequest refreshTokenRequest)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null || !Guid.TryParse(userId, out var uid))
                return Unauthorized();

            await _authService.LogoutAsync(uid);
            return NoContent();
        }

        [Authorize(Roles = "Candidate, Employer, Admin")]
        [HttpGet("me")]
        public async Task<ActionResult<UserResponse>> GetProfileAsync()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if(userId == null)
                return Unauthorized("Invalid email format");

            var userResponse = await _authService.GetUserAsync(Guid.Parse(userId));
            return Ok(userResponse);
        }

        [Authorize(Roles = "Candidate, Employer, Admin")]
        [HttpPut("me")]
        public async Task<ActionResult<User>> UpdateProfileAsync(UpdateProfileRequest request)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (userId == null)
                return Unauthorized("Invalid email format");

            var updatedUser = await _authService.UpdateUserAsync(Guid.Parse(userId), request);
            return Ok(updatedUser);
        }

        // Đổi mật khẩu khi ĐÃ đăng nhập — verify mật khẩu cũ (Identity). Sai old / new yếu → 400.
        [Authorize(Roles = "Candidate, Employer, Admin")]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordRequest request)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null)
                return Unauthorized();

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
                return Unauthorized();

            var result = await _userManager.ChangePasswordAsync(user, request.OldPassword, request.NewPassword);
            if (!result.Succeeded)
                return BadRequest(string.Join(" ", result.Errors.Select(e => e.Description)));

            // Q3 — CHỈ khi mật khẩu đã đổi THẬT. Thu hồi trước (hoặc bất kể kết quả) sẽ đá người dùng
            // gõ nhầm mật khẩu cũ ra khỏi mọi phiên: biến một thao tác vô hại thành mất phiên.
            //
            // KHÔNG truyền HttpContext.RequestAborted: client ngắt kết nối ngay sau khi mật khẩu đã đổi
            // sẽ HUỶ đúng bước thu hồi → mật khẩu mới + phiên cũ còn sống = y nguyên lỗ đang vá.
            await _authService.RevokeAllSessionsAsync(user.Id);

            return NoContent();
        }

        // ── Đặt lại mật khẩu bằng OTP ──────────────────────────────────────────────
        // OTP là credential đặt lại mật khẩu → sinh bằng RNG mã hoá, so khớp hằng-thời-gian,
        // giới hạn số lần đoán, và dùng-một-lần (xoá sạch sau khi đổi mật khẩu thành công).

        private const string OtpProvider = "OTPProvider";
        private const string OtpCodeToken = "OTPCode";        // giữ nguyên tên cũ → OTP đang lưu dở vẫn dùng được
        private const string OtpExpiryToken = "OTPExpiry";
        private const string OtpVerifiedToken = "OtpVerified";
        private const string OtpAttemptsToken = "OtpAttempts";

        private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);

        /// <summary>Sau khi verify-otp, người dùng còn ngần này thời gian để gửi mật khẩu mới.</summary>
        private static readonly TimeSpan OtpVerifiedWindow = TimeSpan.FromMinutes(5);

        /// <summary>OTP chỉ có 10^6 khả năng → không chặn số lần đoán là dò được trong vài phút.</summary>
        private const int MaxOtpAttempts = 5;

        [AllowAnonymous]
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
                return BadRequest("User not found");

            // RandomNumberGenerator, KHÔNG phải `new Random()`: Random gieo hạt theo đồng hồ và là PRNG
            // tuyến tính — biết thời điểm gửi mail là thu hẹp được không gian đoán của một credential
            // đủ sức chiếm tài khoản. "D6" giữ cả số có số 0 đứng đầu → đủ trọn 000000-999999.
            var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

            await _userManager.SetAuthenticationTokenAsync(user, OtpProvider, OtpCodeToken, otp);
            // Ghi theo định dạng round-trip ("O", có Kind=Utc) — `DateTime.UtcNow.ToString()` phụ thuộc
            // culture của tiến trình nên đọc lại ở máy/locale khác là sai giờ hoặc vỡ parse.
            await _userManager.SetAuthenticationTokenAsync(
                user, OtpProvider, OtpExpiryToken, DateTime.UtcNow.Add(OtpLifetime).ToString("O", CultureInfo.InvariantCulture));

            // Dọn trạng thái của lần yêu cầu TRƯỚC: cờ đã-verify cũ không được phép dùng cho OTP mới,
            // và bộ đếm đoán sai phải về 0 cho lượt mới.
            await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpVerifiedToken);
            await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpAttemptsToken);

            await _emailSender.SendEmailAsync(model.Email, "Your OTP Code", BuildEmailBody(otp));

            return Ok("OTP sent to your email");
        }

        [AllowAnonymous]
        [HttpPost("verify-otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null) return BadRequest("User not found");

            var storedOtp = await _userManager.GetAuthenticationTokenAsync(user, OtpProvider, OtpCodeToken);
            var expiryRaw = await _userManager.GetAuthenticationTokenAsync(user, OtpProvider, OtpExpiryToken);

            // Không còn OTP / mốc hết hạn hỏng / đã quá hạn → cùng một thông điệp, không nói rõ vì sao.
            if (string.IsNullOrEmpty(storedOtp) || !TryParseUtc(expiryRaw, out var expiry) || expiry <= DateTime.UtcNow)
                return BadRequest("Invalid or expired OTP");

            if (!await RegisterOtpAttemptAsync(user))
                return BadRequest("Invalid or expired OTP");

            if (!OtpMatches(storedOtp, model.Otp))
                return BadRequest("Invalid or expired OTP");

            await _userManager.SetAuthenticationTokenAsync(
                user, OtpProvider, OtpVerifiedToken, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpAttemptsToken);

            return Ok("OTP verified, you can reset your password");
        }

        [AllowAnonymous]
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null) return BadRequest("User not found");

            // Cửa 1 — đã đi qua verify-otp chưa? Trước đây chỗ này đọc nhầm khoá "OTPCode" (là 6 CHỮ SỐ)
            // rồi DateTime.Parse lên nó → FormatException với MỌI OTP hợp lệ = endpoint 500 vô điều kiện.
            var verifiedRaw = await _userManager.GetAuthenticationTokenAsync(user, OtpProvider, OtpVerifiedToken);
            if (!TryParseUtc(verifiedRaw, out var verifiedAt) || verifiedAt.Add(OtpVerifiedWindow) < DateTime.UtcNow)
                return BadRequest("OTP not verified or expired");

            // Cửa 2 — người gọi có thật sự cầm OTP không? Cửa 1 chỉ khoá theo email: chỉ dựa vào nó thì
            // bất kỳ ai biết email nạn nhân cũng đổi được mật khẩu trong cửa sổ sau khi nạn nhân verify.
            var storedOtp = await _userManager.GetAuthenticationTokenAsync(user, OtpProvider, OtpCodeToken);
            if (string.IsNullOrEmpty(storedOtp) || !OtpMatches(storedOtp, model.Otp))
                return BadRequest("OTP not verified or expired");

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, model.NewPassword);

            // Mật khẩu mới bị Identity từ chối (quá yếu) → GIỮ nguyên OTP để người dùng thử lại mật khẩu
            // khác mà không phải xin mã mới; chỉ đốt OTP khi nó đã thật sự đổi được mật khẩu.
            if (!result.Succeeded)
                return BadRequest(result.Errors);

            // Q3 — thu hồi phiên NGAY sau khi mật khẩu đổi thật, TRƯỚC bước đốt OTP: bốn lệnh xoá token
            // bên dưới đều có thể ném, và bỏ lỡ bước thu hồi (kẻ chiếm tài khoản gia hạn phiên vô hạn)
            // nặng hơn bỏ lỡ bước đốt OTP (mã còn sống thêm vài phút trong tay người ĐÃ cầm nó).
            await _authService.RevokeAllSessionsAsync(user.Id);

            await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpCodeToken);
            await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpExpiryToken);
            await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpVerifiedToken);
            await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpAttemptsToken);

            return Ok("Password reset successful");
        }

        /// <summary>
        /// So khớp OTP HẰNG-THỜI-GIAN (mẫu đã dùng cho X-Internal-Token ở Payment/Interview):
        /// `==` trên string thoát sớm ở ký tự lệch đầu tiên → rò rỉ tiền tố đúng qua thời gian đáp ứng.
        /// </summary>
        private static bool OtpMatches(string stored, string? supplied)
        {
            if (string.IsNullOrEmpty(supplied)) return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(supplied));
        }

        /// <summary>
        /// Đếm lượt đoán; quá <see cref="MaxOtpAttempts"/> thì đốt luôn OTP (phải xin mã mới).
        /// Trả về false khi lượt này KHÔNG được phép so khớp nữa.
        /// </summary>
        private async Task<bool> RegisterOtpAttemptAsync(User user)
        {
            var raw = await _userManager.GetAuthenticationTokenAsync(user, OtpProvider, OtpAttemptsToken);
            _ = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts);
            attempts++;

            if (attempts > MaxOtpAttempts)
            {
                _logger.LogWarning("OTP bị đoán quá {Max} lần cho user {UserId} — huỷ mã.", MaxOtpAttempts, user.Id);
                await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpCodeToken);
                await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpExpiryToken);
                await _userManager.RemoveAuthenticationTokenAsync(user, OtpProvider, OtpAttemptsToken);
                return false;
            }

            await _userManager.SetAuthenticationTokenAsync(
                user, OtpProvider, OtpAttemptsToken, attempts.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
        /// Đọc mốc thời gian UTC. Nhận cả định dạng round-trip mới lẫn giá trị CŨ ghi bằng
        /// `DateTime.UtcNow.ToString()` (không mang Kind) — giá trị cũ vốn là UTC nên gắn Kind=Utc.
        /// TryParse (không phải Parse) để chuỗi rác trả 400 chứ không ném 500.
        /// </summary>
        private static bool TryParseUtc(string? raw, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var parsed))
                return false;

            utc = parsed.Kind switch
            {
                DateTimeKind.Utc => parsed,
                DateTimeKind.Local => parsed.ToUniversalTime(),
                _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            };
            return true;
        }

        private static string BuildEmailBody(string otp) =>
            $"""
            <div style="font-family:Arial,sans-serif;max-width:480px;margin:auto;padding:32px;
                        border:1px solid #e5e7eb;border-radius:8px">
              <h2 style="color:#1d4ed8;margin-bottom:8px">Password Reset Request</h2>
              <p style="color:#374151">
                Use the code below to reset your password.
                It expires in <strong>5 minutes</strong>.
              </p>
              <div style="background:#f3f4f6;border-radius:8px;padding:24px;
                          text-align:center;margin:24px 0">
                <span style="font-size:40px;font-weight:bold;letter-spacing:12px;
                             color:#1d4ed8">{otp}</span>
              </div>
              <p style="color:#6b7280;font-size:13px">
                If you didn't request this, you can safely ignore this email.
              </p>
            </div>
            """;
    }
}
