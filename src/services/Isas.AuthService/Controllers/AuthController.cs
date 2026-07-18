using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
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
        private readonly ILogger<AuthController> _logger;
        public AuthController(IAuthService authService, UserManager<User> userManager, SignInManager<User> signInManager, IEmailSender emailSender,
            IGoogleLoginRedirects googleRedirects, ILogger<AuthController> logger)
        {
            _authService = authService;
            _userManager = userManager;
            _signInManager = signInManager;
            _emailSender = emailSender;
            _googleRedirects = googleRedirects;
            _logger = logger;
        }

        // A5 — auth-entry công khai (chưa có JWT): AllowAnonymous tường minh (rõ ý định + phòng tương lai).
        [AllowAnonymous]
        [HttpPost("register")]
        public async Task<ActionResult<AuthResponse>> RegisterAsync(RegisterRequest registerRequest)
        {
            var existingUser = await _userManager.FindByEmailAsync(registerRequest.Email);

            if(existingUser != null)
            {
                return BadRequest("Email already exists");
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
                return BadRequest("Email already exists");

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

            return Ok(await _authService.LoginAsync(request));
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
        // Thay vào đó 302 về FE, token nằm ở FRAGMENT (fragment không được gửi lên server → không
        // lọt access log / Referer). Đích lấy từ config server, không nhận host từ client.
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
            catch (Exception ex)
            {
                // Không để lộ chi tiết lỗi ra URL — chỉ mã lỗi cho FE hiển thị, chi tiết vào log.
                _logger.LogError(ex, "Đăng nhập Google thất bại khi phát hành phiên");
                return Redirect(_googleRedirects.FailureUrl("login_failed"));
            }

            // Cookie external chỉ phục vụ 1 vòng OAuth — dọn ngay để không còn dấu vết phiên.
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            return Redirect(_googleRedirects.SuccessUrl(authResponse, returnUrl));
        }

        [AllowAnonymous]
        [HttpPost("refresh")]
        public async Task<ActionResult<RefreshTokenResponse>> RefreshTokenAsync(RefreshTokenRequest refreshTokenRequest)
        {
            var existingRefreshToken = await _authService.GetRefreshTokenAsync(refreshTokenRequest.RefreshToken);

            if(existingRefreshToken == null || existingRefreshToken.IsRevoked || existingRefreshToken.ExpiresAt < DateTime.UtcNow)
            {
                return Unauthorized("Refresh token expired or revoked");
            }

            var result = await _authService.RefreshTokenAsync(refreshTokenRequest.RefreshToken);

            return Ok(result);
        }

        [Authorize(Roles = "Candidate, Employer, Admin")]
        [HttpPost("logout")]
        public async Task<IActionResult> LogoutAsync(RefreshTokenRequest refreshTokenRequest)
        {
            await _authService.LogoutAsync(refreshTokenRequest.RefreshToken);
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

            return NoContent();
        }

        [AllowAnonymous]
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
                return BadRequest("User not found");

            var otp = new Random().Next(100000, 999999).ToString();

            await _userManager.SetAuthenticationTokenAsync(user, "OTPProvider", "OTPCode", otp);
            await _userManager.SetAuthenticationTokenAsync(user, "OTPProvider", "OTPExpiry", DateTime.UtcNow.AddMinutes(5).ToString());

            await _emailSender.SendEmailAsync(model.Email, "Your OTP Code", BuildEmailBody(otp));

            return Ok("OTP sent to your email");
        }

        [AllowAnonymous]
        [HttpPost("verify-otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null) return BadRequest("User not found");

            var storedOtp = await _userManager.GetAuthenticationTokenAsync(user, "OTPProvider", "OTPCode");
            var expiry = await _userManager.GetAuthenticationTokenAsync(user, "OTPProvider", "OTPExpiry");

            if (storedOtp == model.Otp && DateTime.Parse(expiry) > DateTime.UtcNow)
            {
                await _userManager.SetAuthenticationTokenAsync(user, "OTPProvider", "OtpVerified", DateTime.UtcNow.ToString());
                return Ok("OTP verified, you can reset your password");
            }

            return BadRequest("Invalid or expired OTP");
        }

        [AllowAnonymous]
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null) return BadRequest("User not found");

            var verifiedOtp = await _userManager.GetAuthenticationTokenAsync(user, "OTPProvider", "OTPCode");
            if (string.IsNullOrEmpty(verifiedOtp) || DateTime.Parse(verifiedOtp).AddMinutes(5) < DateTime.UtcNow)
                return BadRequest("OTP not verified or expired");

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, model.NewPassword);

            if (result.Succeeded)
                return Ok("Password reset successful");

            return BadRequest(result.Errors);
        }

        private static string BuildEmailBody(string otp) =>
            $"""
            <div style="font-family:Arial,sans-serif;max-width:480px;margin:auto;padding:32px;
                        border:1px solid #e5e7eb;border-radius:8px">
              <h2 style="color:#1d4ed8;margin-bottom:8px">Password Reset Request</h2>
              <p style="color:#374151">
                Use the code below to reset your password.
                It expires in <strong>10 minutes</strong>.
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
