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

        // A6: OrgAdmin tạo HrMember (passwordless) trong org của mình; email đã có account → OrgMemberConflictException.
        Task<OrgMemberResponse> AddOrgMemberAsync(Guid orgId, string email, string? fullName, CancellationToken ct = default);

        // A6: liệt kê thành viên (email + org_role + joinedAt) của 1 org.
        Task<IReadOnlyList<OrgMemberResponse>> ListOrgMembersAsync(Guid orgId, CancellationToken ct = default);

        // A6b: OrgAdmin đổi org_role thành viên trong org mình. Không thuộc org → OrgMemberNotFoundException;
        // hạ cấp OrgAdmin cuối cùng → OrgMemberConflictException.
        Task<OrgMemberResponse> ChangeOrgMemberRoleAsync(Guid orgId, Guid userId, OrgRole newRole, CancellationToken ct = default);

        // A6b: OrgAdmin xoá thành viên khỏi org mình (hard-remove row, account giữ nguyên). Không thuộc org →
        // OrgMemberNotFoundException; xoá OrgAdmin cuối cùng → OrgMemberConflictException.
        Task RemoveOrgMemberAsync(Guid orgId, Guid userId, CancellationToken ct = default);
        Task<AuthResponse> LoginAsync(LoginRequest loginRequest);
        Task<AuthResponse> LoginGoogleAsync(ExternalLoginInfo info);
        Task<AuthResponse> RefreshTokenAsync(string refreshToken);
        Task LogoutAsync(string refreshToken);
        Task<UserResponse> GetUserAsync(Guid userId);
        Task<string> UpdateUserAsync(Guid userId, UpdateProfileRequest request);
        Task<RefreshToken> GetRefreshTokenAsync(string refreshToken);

    }
}
