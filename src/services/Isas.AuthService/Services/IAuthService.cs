using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Microsoft.AspNetCore.Identity;

namespace Isas.AuthService.Services
{
    public interface IAuthService
    {
        Task<AuthResponse> RegisterAsync(RegisterRequest registerRequest);
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

        // PlatformAdmin oversight (AUTH-7) — liệt kê MỌI org (cross-org, read-only); optional lọc theo Name.
        Task<IReadOnlyList<OrganizationResponse>> ListAllOrganizationsAsync(string? search, CancellationToken ct = default);

        // PlatformAdmin oversight (AUTH-7) — liệt kê MỌI user (cross-org) + role + membership; optional lọc role/email.
        Task<IReadOnlyList<AdminUserResponse>> ListAllUsersAsync(string? role, string? search, CancellationToken ct = default);

        // Thông tin tổ chức: đọc (mọi member) + sửa name/taxCode (OrgAdmin — enforce ở controller).
        // Org không tồn tại → KeyNotFoundException.
        Task<OrganizationResponse> GetOrganizationAsync(Guid orgId, CancellationToken ct = default);
        Task<OrganizationResponse> UpdateOrganizationAsync(Guid orgId, UpdateOrgRequest request, CancellationToken ct = default);

        Task<AuthResponse> LoginAsync(LoginRequest loginRequest);
        Task<AuthResponse> LoginGoogleAsync(ExternalLoginInfo info);
        Task<AuthResponse> RefreshTokenAsync(string refreshToken);
        Task LogoutAsync(string refreshToken);
        Task<UserResponse> GetUserAsync(Guid userId);
        Task<string> UpdateUserAsync(Guid userId, UpdateProfileRequest request);
        Task<RefreshToken> GetRefreshTokenAsync(string refreshToken);

    }
}
