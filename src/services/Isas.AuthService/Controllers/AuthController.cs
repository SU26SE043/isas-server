using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
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
        public AuthController(IAuthService authService, UserManager<User> userManager, SignInManager<User> signInManager, IEmailSender emailSender)
        {
            _authService = authService;
            _userManager = userManager;
            _signInManager = signInManager;
            _emailSender = emailSender;
        }

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

        [Authorize]
        [HttpPost("logout")]
        public async Task<IActionResult> LogoutAsync(RefreshTokenRequest refreshTokenRequest)
        {
            await _authService.LogoutAsync(refreshTokenRequest.RefreshToken);
            return NoContent();
        }


        [Authorize]
        [HttpGet("me")]
        public async Task<ActionResult<UserResponse>> GetProfileAsync()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if(userId == null)
                return Unauthorized("Invalid email format");

            var userResponse = await _authService.GetUserAsync(Guid.Parse(userId));
            return Ok(userResponse);
        }

        [Authorize]
        [HttpPut("me")]
        public async Task<ActionResult<User>> UpdateProfileAsync(UpdateProfileRequest request)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (userId == null)
                return Unauthorized("Invalid email format");

            var updatedUser = await _authService.UpdateUserAsync(Guid.Parse(userId), request);
            return Ok(updatedUser);
        }

        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
                return BadRequest("User not found");

            var otp = new Random().Next(100000, 999999).ToString();

            await _userManager.SetAuthenticationTokenAsync(user, "OTPProvider", "OTPCode", otp);
            await _userManager.SetAuthenticationTokenAsync(user, "OTPProvider", "OTPExpiry", DateTime.UtcNow.AddMinutes(5).ToString());

            await _emailSender.SendEmailAsync(model.Email, "Your OTP Code", $"Your OTP is {otp}");

            return Ok("OTP sent to your email");
        }

        [HttpPost("verify-otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null) return BadRequest("User not found");

            var storedOtp = await _userManager.GetAuthenticationTokenAsync(user, "OTPProvider", "OTPCode");
            var expiry = await _userManager.GetAuthenticationTokenAsync(user, "OTPProvider", "OTPExpiry");

            if (storedOtp == model.Otp && DateTime.Parse(expiry) > DateTime.UtcNow)
            {
                return Ok("OTP verified, you can reset your password");
            }

            return BadRequest("Invalid or expired OTP");
        }

        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null) return BadRequest("User not found");

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, model.NewPassword);

            if (result.Succeeded)
                return Ok("Password reset successful");

            return BadRequest(result.Errors);
        }
    }
}
