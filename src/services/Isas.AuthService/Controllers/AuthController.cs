using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Mvc;

namespace issa.AuthService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("register")]
        public async Task<ActionResult<AuthResponse>> RegisterAsync(RegisterRequest registerRequest)
        {
            var result = await _authService.RegisterAsync(registerRequest);

            return Ok(result);
        }

        [HttpPost("login")]
        public async Task<ActionResult<AuthResponse>> LoginAsync(LoginRequest loginRequest)
        {
            var result = await _authService.LoginAsync(loginRequest);

            return Ok(result);
        }

        [HttpPost("refresh")]
        public async Task<ActionResult<AuthResponse>> RefreshTokenAsync(RefreshTokenRequest refreshTokenRequest)
        {
            var result = await _authService.RefreshTokenAsync(refreshTokenRequest.RefreshToken);
            return Ok(result);
        }

        [HttpPost("logout")]
        public async Task<IActionResult> LogoutAsync(RefreshTokenRequest refreshTokenRequest)
        {
            await _authService.LogoutAsync(refreshTokenRequest.RefreshToken);
            return NoContent();
        }
    }
}
