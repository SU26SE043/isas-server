using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Services
{
    public interface IAuthService
    {
        Task<string> RegisterAsync(RegisterRequest registerRequest);
        Task<AuthResponse> RegisterOrgAsync(RegisterOrgRequest request);

        // D2: tạo-hoặc-lấy account Candidate nhẹ theo email (idempotent) → { candidateId, accessToken }.
        Task<ProvisionCandidateResponse> ProvisionCandidateAsync(string email, string? fullName, CancellationToken ct = default);
        Task<AuthResponse> LoginAsync(LoginRequest loginRequest);
        Task<AuthResponse> LoginGoogleAsync(ExternalLoginInfo info);
        Task<AuthResponse> RefreshTokenAsync(string refreshToken);
        Task LogoutAsync(string refreshToken);
        Task<UserResponse> GetUserAsync(Guid userId);
        Task<string> UpdateUserAsync(Guid userId, UpdateProfileRequest request);
        Task<RefreshToken> GetRefreshTokenAsync(string refreshToken);

    }
}
