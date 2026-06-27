using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Services
{
    public interface IAuthService
    {
        Task<string> RegisterAsync(RegisterRequest registerRequest);
        Task<AuthResponse> LoginAsync(LoginRequest loginRequest);
        Task<AuthResponse> LoginGoogleAsync(ExternalLoginInfo info);
        Task<AuthResponse> RefreshTokenAsync(string refreshToken);
        Task LogoutAsync(string refreshToken);
        Task<UserResponse> GetUserAsync(Guid userId);
        Task<string> UpdateUserAsync(Guid userId, UpdateProfileRequest request);
        Task<RefreshToken> GetRefreshTokenAsync(string refreshToken);

    }
}
