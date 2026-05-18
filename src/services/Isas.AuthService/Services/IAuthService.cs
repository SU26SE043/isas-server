using Isas.AuthService.DTOs;

namespace Isas.AuthService.Services
{
    public interface IAuthService
    {
        Task<AuthResponse> RegisterAsync(RegisterRequest registerRequest);
        Task<AuthResponse> LoginAsync(LoginRequest loginRequest);
        Task<AuthResponse> RefreshTokenAsync(string refreshToken);
        Task LogoutAsync(string refreshToken);
    }
}
