using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using System.ComponentModel.DataAnnotations;

namespace Isas.AuthService.Services
{
    public interface IAuthService
    {
        Task<string> RegisterAsync(RegisterRequest registerRequest);
        Task<AuthResponse> LoginAsync(LoginRequest loginRequest);
        Task<AuthResponse> RefreshTokenAsync(string refreshToken);
        Task LogoutAsync(string refreshToken);
        Task<UserResponse> GetUserAsync(Guid userId);
        Task<string> UpdateUserAsync(Guid userId, UpdateProfileRequest request);
        Task<RefreshToken> GetRefreshTokenAsync(string refreshToken);
        //Task SendForgotPasswordOtpAsync(string toEmail, string otp);
    }
}
