using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.Shared.Pagination;
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
        // Keyset-paged (DB8): (CreatedAt DESC, Id DESC); cursor rỗng = trang đầu; limit mặc định 500.
        Task<KeysetPage<OrganizationResponse>> ListAllOrganizationsAsync(string? search, string? cursor, int? limit, CancellationToken ct = default);

        // PlatformAdmin oversight (AUTH-7) — liệt kê MỌI user (cross-org) + role + membership; optional lọc role/email.
        // Keyset-paged (DB8): (CreatedAt DESC, Id DESC); role lọc TRONG query (push-down) → phân trang đúng.
        Task<KeysetPage<AdminUserResponse>> ListAllUsersAsync(string? role, string? search, string? cursor, int? limit, CancellationToken ct = default);

        // F20 (FR16) — PlatformAdmin đình chỉ / gỡ đình chỉ account. Ban chặn MỌI đường phát phiên mới
        // + thu hồi refresh token; access token đang lưu hành vẫn sống tới hết TTL (GEN-3 — validate
        // offline, không thu hồi được). User không tồn tại → KeyNotFoundException; đình chỉ Admin hoạt
        // động CUỐI CÙNG → AdminActionConflictException.
        Task<AdminUserResponse> BanUserAsync(Guid actingAdminId, Guid userId, string? reason, CancellationToken ct = default);
        Task<AdminUserResponse> UnbanUserAsync(Guid userId, CancellationToken ct = default);

        // F20 — PlatformAdmin đặt lại mật khẩu hộ user; thu hồi refresh token (phiên cũ phải chết,
        // nếu không thì đổi mật khẩu không đuổi được kẻ đang chiếm tài khoản). Mật khẩu yếu →
        // ArgumentException; user không tồn tại → KeyNotFoundException.
        Task AdminResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default);

        // PlatformAdmin đổi platform-role của user (AUTH-3). Thay THẾ role hiện tại (mô hình 1 role/user)
        // rồi thu hồi refresh token theo AUTH-5 — quyền mới có hiệu lực sau ≤1 TTL access token (15').
        // Role ngoài {Candidate, Employer, Admin} → ArgumentException; user không tồn tại →
        // KeyNotFoundException; hạ cấp Admin hoạt động CUỐI CÙNG, hoặc rời Employer khi còn là thành
        // viên org → AdminActionConflictException.
        Task<AdminUserResponse> ChangePlatformRoleAsync(Guid userId, string newRole, CancellationToken ct = default);

        // Thông tin tổ chức: đọc (mọi member) + sửa name/taxCode (OrgAdmin — enforce ở controller).
        // Org không tồn tại → KeyNotFoundException.
        Task<OrganizationResponse> GetOrganizationAsync(Guid orgId, CancellationToken ct = default);
        Task<OrganizationResponse> UpdateOrganizationAsync(Guid orgId, UpdateOrgRequest request, CancellationToken ct = default);

        Task<AuthResponse> LoginAsync(LoginRequest loginRequest);
        Task<AuthResponse> LoginGoogleAsync(ExternalLoginInfo info);
        Task<AuthResponse> RefreshTokenAsync(string refreshToken);
        // Đăng xuất theo USER (không theo 1 token): thu hồi MỌI refresh token của user — tab khác không
        // gia hạn phiên tiếp được. Access token đang lưu hành vẫn sống tới hết TTL (validate offline GEN-3).
        Task LogoutAsync(Guid userId);

        // Q3 — thu hồi MỌI phiên của user vì lý do BẢO MẬT (đổi mật khẩu / đặt lại mật khẩu), tách tên
        // khỏi LogoutAsync dù thân giống hệt: hai đường đổi mật khẩu ở AuthController thao tác thẳng
        // trên UserManager nên không đi qua service, và gọi "logout" cho việc đuổi kẻ chiếm tài khoản
        // là mượn nghĩa. Access token đang lưu hành vẫn sống tới hết TTL (GEN-3).
        Task RevokeAllSessionsAsync(Guid userId, CancellationToken ct = default);
        Task<UserResponse> GetUserAsync(Guid userId);
        Task<string> UpdateUserAsync(Guid userId, UpdateProfileRequest request);
        Task<RefreshToken> GetRefreshTokenAsync(string refreshToken);

    }
}
