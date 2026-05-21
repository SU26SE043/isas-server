using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Isas.AuthService.Controllers
{
    [Route("auth")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly UserManager<User> _userManager;
        private readonly SignInManager<User> _signInManager;
        public AuthController(IAuthService authService, UserManager<User> userManager, SignInManager<User> signInManager)
        {
            _authService = authService;
            _userManager = userManager;
            _signInManager = signInManager;
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
            {
                return Unauthorized("Invalid credentials");
            }

            var result = await _signInManager.PasswordSignInAsync(
                user.UserName,
                request.Password,
                isPersistent: false,
                lockoutOnFailure: true);

            if (result.Succeeded)
            {
                return Ok(await _authService.LoginAsync(request));
            }

            if (result.IsLockedOut)
            {
                return Unauthorized("Account locked");
            }

            return Unauthorized("Invalid credentials");
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
    }
}
