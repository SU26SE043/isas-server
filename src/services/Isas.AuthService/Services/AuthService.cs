using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Isas.AuthService.Services
{
    public class AuthService : IAuthService
    {
        private readonly AuthDbContext _authDbContext;
        private readonly IJwtService _jwtService;
        private readonly UserManager<User> _userManager;
        private readonly RoleManager<Role> _roleManager;
        private readonly IConfiguration _configuration;
        private readonly SignInManager<User> _signInManager;

        public AuthService(AuthDbContext authDbContext, IJwtService jwtService,
            UserManager<User> userManager, RoleManager<Role> roleManager,
            IConfiguration configuration, SignInManager<User> signInManager
            )
        {
            _authDbContext = authDbContext;
            _jwtService = jwtService;
            _userManager = userManager;
            _roleManager = roleManager;
            _configuration = configuration;
            _signInManager = signInManager;
        }

        public async Task<AuthResponse> LoginAsync(LoginRequest loginRequest)
        {
            var user = await _userManager.FindByEmailAsync(loginRequest.Email);
            if (user is null)
                throw new UnauthorizedAccessException("Invalid credentials");

            return await GenerateAuthResponse(user);
        }

        public async Task<AuthResponse> LoginGoogleAsync(ExternalLoginInfo info)
        {
            var result = await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, false);

            if (result.Succeeded)
            {
                var user = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                return await GenerateAuthResponse(user);
            }

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrEmpty(email))
                throw new Exception("Google account does not provide an email");

            var newUser = new User
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                FullName = info.Principal.Identity?.Name ?? email,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var identityResult = await _userManager.CreateAsync(newUser);
            if (!identityResult.Succeeded)
                throw new Exception(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

            await _userManager.AddLoginAsync(newUser, info);
            await EnsureRoleExistsAsync("Candidate");
            await _userManager.AddToRoleAsync(newUser, "Candidate");

            return await GenerateAuthResponse(newUser);
        }

        public async Task LogoutAsync(string refreshToken)
        {
            var hashedToken = _jwtService.HashRefreshToken(refreshToken);

            var existingToken = await _authDbContext.RefreshTokens
                .FirstOrDefaultAsync(x => x.Token == hashedToken && !x.IsRevoked);

            if (existingToken is null) return;

            existingToken.IsRevoked = true;
            await _authDbContext.SaveChangesAsync();
        }

        public async Task<AuthResponse> RefreshTokenAsync(string refreshToken)
        {
            var hashedToken = _jwtService.HashRefreshToken(refreshToken);

            var existingToken = await _authDbContext.RefreshTokens
                .Include(x => x.User)
                .FirstOrDefaultAsync(x => x.Token == hashedToken && !x.IsRevoked);

            if (existingToken is null)
                throw new UnauthorizedAccessException("Invalid refresh token");

            if (existingToken.ExpiresAt < DateTime.UtcNow)
                throw new UnauthorizedAccessException("Refresh token expired");

            existingToken.IsRevoked = true;

            var newRawRefreshToken = _jwtService.GenerateRefreshToken();
            var newRefreshTokenEntity = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = existingToken.UserId,
                Token = _jwtService.HashRefreshToken(newRawRefreshToken),
                ExpiresAt = DateTime.UtcNow.AddDays(GetRefreshTokenDays()),
                CreatedAt = DateTime.UtcNow
            };

            existingToken.ReplacedBy = newRefreshTokenEntity.Id;
            _authDbContext.RefreshTokens.Add(newRefreshTokenEntity);
            await _authDbContext.SaveChangesAsync();

            var roles = await _userManager.GetRolesAsync(existingToken.User);
            var membership = await GetMembershipAsync(existingToken.UserId);
            var accessToken = _jwtService.GenerateAccessToken(existingToken.User, roles, membership);

            return BuildAuthResponse(accessToken, newRawRefreshToken);
        }

        // AUTH-1: register → role Candidate mặc định. Trả AuthResponse {accessToken, refreshToken,
        // expiresAt} (như Login/RegisterOrg) qua GenerateAuthResponse — frontend nhận token ngay khi
        // đăng ký, không phải login lại. Dùng chung đường sinh token (không lặp logic JWT).
        public async Task<AuthResponse> RegisterAsync(RegisterRequest registerRequest)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = registerRequest.Email,
                Email = registerRequest.Email,
                FullName = registerRequest.FullName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var result = await _userManager.CreateAsync(user, registerRequest.Password);
            if (!result.Succeeded)
                throw new Exception(string.Join("; ", result.Errors.Select(e => e.Description)));

            await EnsureRoleExistsAsync("Candidate");
            await _userManager.AddToRoleAsync(user, "Candidate");

            return await GenerateAuthResponse(user);
        }

        // A3: đăng ký tổ chức → user (role Employer) + Organization + OrgMember(OrgAdmin); trả AuthResponse
        // (token tự mang org_id + org_role nhờ A2 vì membership đã persist trước GenerateAuthResponse).
        public async Task<AuthResponse> RegisterOrgAsync(RegisterOrgRequest request)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = request.Email,
                Email = request.Email,
                FullName = request.FullName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var result = await _userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
                throw new Exception(string.Join("; ", result.Errors.Select(e => e.Description)));

            await EnsureRoleExistsAsync("Employer");
            await _userManager.AddToRoleAsync(user, "Employer");

            var org = new Organization
            {
                Id = Guid.NewGuid(),
                Name = request.OrgName,
                TaxCode = request.TaxCode,
                CreatedAt = DateTime.UtcNow
            };
            var member = new OrgMember
            {
                OrgId = org.Id,
                UserId = user.Id,
                OrgRole = OrgRole.OrgAdmin,
                JoinedAt = DateTime.UtcNow
            };

            _authDbContext.Organizations.Add(org);
            _authDbContext.OrgMembers.Add(member);
            await _authDbContext.SaveChangesAsync();

            return await GenerateAuthResponse(user);
        }

        // D2: provision Candidate nhẹ (internal). Create-or-get theo email (chuẩn hoá qua
        // FindByEmailAsync — Identity so khớp NormalizedEmail): chưa có → tạo User KHÔNG mật khẩu
        // (mẫu LoginGoogle: CreateAsync(user) không password) + role Candidate; đã có → dùng lại.
        // Trả candidateId (ref lỏng xuyên service) + JWT (mang role thật của account). Idempotent —
        // gọi lại cùng email → cùng candidateId.
        public async Task<ProvisionCandidateResponse> ProvisionCandidateAsync(
            string email, string? fullName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Email is required", nameof(email));

            email = email.Trim();
            var user = await _userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    UserName = email,
                    Email = email,
                    FullName = string.IsNullOrWhiteSpace(fullName) ? email : fullName!.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                var result = await _userManager.CreateAsync(user);   // KHÔNG mật khẩu (magic-link)
                if (!result.Succeeded)
                    throw new Exception(string.Join("; ", result.Errors.Select(e => e.Description)));

                await EnsureRoleExistsAsync("Candidate");
                await _userManager.AddToRoleAsync(user, "Candidate");
            }

            var roles = await _userManager.GetRolesAsync(user);
            var membership = await GetMembershipAsync(user.Id);
            var accessToken = _jwtService.GenerateAccessToken(user, roles, membership);

            return new ProvisionCandidateResponse
            {
                CandidateId = user.Id,
                AccessToken = accessToken
            };
        }

        // A6: OrgAdmin mời/tạo HrMember vào org của mình (AUTH-4/AUTH-8). Email đã có account (đã là
        // thành viên org này, hoặc email đã đăng ký nơi khác — email UNIQUE) → OrgMemberConflictException.
        // Tạo User KHÔNG mật khẩu (mẫu ProvisionCandidate — HR đặt mật khẩu qua forgot/reset) + role
        // Employer + OrgMember(HrMember, OrgId = org của caller). Membership persist → login sau mang
        // org_id + org_role=HrMember (A2), A4 chặn billing đúng.
        public async Task<OrgMemberResponse> AddOrgMemberAsync(
            Guid orgId, string email, string? fullName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Email is required", nameof(email));

            email = email.Trim();

            var existing = await _userManager.FindByEmailAsync(email);
            if (existing is not null)
            {
                var alreadyMember = await _authDbContext.OrgMembers
                    .AnyAsync(m => m.OrgId == orgId && m.UserId == existing.Id, ct);
                throw new OrgMemberConflictException(alreadyMember
                    ? "Email is already a member of this organization"
                    : "Email is already registered");
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                FullName = string.IsNullOrWhiteSpace(fullName) ? email : fullName!.Trim(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var result = await _userManager.CreateAsync(user);   // KHÔNG mật khẩu (đặt qua forgot/reset)
            if (!result.Succeeded)
                throw new Exception(string.Join("; ", result.Errors.Select(e => e.Description)));

            await EnsureRoleExistsAsync("Employer");
            await _userManager.AddToRoleAsync(user, "Employer");

            var member = new OrgMember
            {
                OrgId = orgId,
                UserId = user.Id,
                OrgRole = OrgRole.HrMember,
                JoinedAt = DateTime.UtcNow
            };
            _authDbContext.OrgMembers.Add(member);
            await _authDbContext.SaveChangesAsync(ct);

            return new OrgMemberResponse
            {
                UserId = user.Id,
                Email = user.Email!,
                FullName = user.FullName,
                OrgRole = OrgRole.HrMember.ToString(),
                JoinedAt = member.JoinedAt
            };
        }

        // A6: thành viên org (email + org_role + joined_at thật — A6b). Materialize rồi project
        // client-side (enum→string ToString không dịch được sang SQL với mọi provider).
        public async Task<OrganizationResponse> GetOrganizationAsync(Guid orgId, CancellationToken ct = default)
        {
            var org = await _authDbContext.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orgId, ct)
                ?? throw new KeyNotFoundException("Tổ chức không tồn tại.");

            var memberCount = await _authDbContext.OrgMembers.CountAsync(m => m.OrgId == orgId, ct);
            return ToOrgResponse(org, memberCount);
        }

        public async Task<OrganizationResponse> UpdateOrganizationAsync(
            Guid orgId, UpdateOrgRequest request, CancellationToken ct = default)
        {
            var org = await _authDbContext.Organizations
                .FirstOrDefaultAsync(o => o.Id == orgId, ct)
                ?? throw new KeyNotFoundException("Tổ chức không tồn tại.");

            if (!string.IsNullOrWhiteSpace(request.Name))
                org.Name = request.Name.Trim();
            if (request.TaxCode is not null)
                org.TaxCode = string.IsNullOrWhiteSpace(request.TaxCode) ? null : request.TaxCode.Trim();

            await _authDbContext.SaveChangesAsync(ct);

            var memberCount = await _authDbContext.OrgMembers.CountAsync(m => m.OrgId == orgId, ct);
            return ToOrgResponse(org, memberCount);
        }

        // AUTH-7: PlatformAdmin liệt kê MỌI org (cross-org, read-only). Keyset-paged (DB8): mới nhất trước
        // theo (CreatedAt DESC, Id DESC); cursor rỗng = trang đầu; limit mặc định 500 (giữ hành vi cũ).
        // Optional lọc theo Name (contains, case-insensitive). MemberCount đếm gộp 1 lần (GroupBy) tránh N+1.
        public async Task<KeysetPage<OrganizationResponse>> ListAllOrganizationsAsync(
            string? search, string? cursor, int? limit, CancellationToken ct = default)
        {
            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);

            var query = _authDbContext.Organizations.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(o => o.Name.ToLower().Contains(term.ToLower()));
            }
            if (cur is not null)
                query = query.Where(o => o.CreatedAt < cur.CreatedAt
                    || (o.CreatedAt == cur.CreatedAt && o.Id.CompareTo(cur.Id) < 0));

            var orgs = await query
                .OrderByDescending(o => o.CreatedAt)
                .ThenByDescending(o => o.Id)
                .Take(take)
                .ToListAsync(ct);

            var orgIds = orgs.Select(o => o.Id).ToList();
            var counts = await _authDbContext.OrgMembers
                .AsNoTracking()
                .Where(m => orgIds.Contains(m.OrgId))
                .GroupBy(m => m.OrgId)
                .Select(g => new { OrgId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.OrgId, x => x.Count, ct);

            var items = orgs
                .Select(o => ToOrgResponse(o, counts.TryGetValue(o.Id, out var c) ? c : 0))
                .ToList();
            var next = orgs.Count == take
                ? new KeysetCursor(orgs[^1].CreatedAt, orgs[^1].Id).Encode()
                : null;
            return new KeysetPage<OrganizationResponse>(items, next);
        }

        // AUTH-7: PlatformAdmin liệt kê MỌI user (cross-org, read-only). Keyset-paged (DB8): mới nhất trước
        // theo (CreatedAt DESC, Id DESC); cursor rỗng = trang đầu; limit mặc định 500. Optional lọc email.
        // Role platform hiển thị lấy qua GetRolesAsync; membership org (OrgId/OrgName/OrgRole) join OrgMembers
        // ⊕ Organizations — null nếu không thuộc org. Lọc `role` PUSH-DOWN vào query (resolve name→id qua
        // Identity normalizer, join UserRoles) TRƯỚC keyset+limit → phân trang đúng (cũ: lọc sau cap 500 = sai).
        public async Task<KeysetPage<AdminUserResponse>> ListAllUsersAsync(
            string? role, string? search, string? cursor, int? limit, CancellationToken ct = default)
        {
            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);

            Guid? roleId = null;
            if (!string.IsNullOrWhiteSpace(role))
            {
                var roleEntity = await _roleManager.FindByNameAsync(role.Trim());
                if (roleEntity is null)
                    return KeysetPage<AdminUserResponse>.Empty;   // role lạ → không user nào khớp
                roleId = roleEntity.Id;
            }

            var query = _authDbContext.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(u => u.Email != null && u.Email.ToLower().Contains(term.ToLower()));
            }
            if (roleId is Guid rid)
                query = query.Where(u => _authDbContext.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == rid));
            if (cur is not null)
                query = query.Where(u => u.CreatedAt < cur.CreatedAt
                    || (u.CreatedAt == cur.CreatedAt && u.Id.CompareTo(cur.Id) < 0));

            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .ThenByDescending(u => u.Id)
                .Take(take)
                .ToListAsync(ct);

            var userIds = users.Select(u => u.Id).ToList();
            var memberships = await _authDbContext.OrgMembers
                .AsNoTracking()
                .Where(m => userIds.Contains(m.UserId))
                .Include(m => m.Organization)
                .ToListAsync(ct);
            var membershipByUser = memberships
                .GroupBy(m => m.UserId)
                .ToDictionary(g => g.Key, g => g.First());

            var result = new List<AdminUserResponse>(users.Count);
            foreach (var u in users)
            {
                // role đã lọc ở tầng query (push-down) — chỉ resolve để HIỂN THỊ (tập ≤ take, không N+1 lớn).
                var userRole = (await _userManager.GetRolesAsync(u)).FirstOrDefault() ?? "No role";

                membershipByUser.TryGetValue(u.Id, out var m);
                result.Add(new AdminUserResponse
                {
                    Id = u.Id,
                    Email = u.Email,
                    FullName = u.FullName,
                    Role = userRole,
                    OrgId = m?.OrgId,
                    OrgName = m?.Organization?.Name,
                    OrgRole = m?.OrgRole.ToString(),
                    CreatedAt = u.CreatedAt
                });
            }

            var next = users.Count == take
                ? new KeysetCursor(users[^1].CreatedAt, users[^1].Id).Encode()
                : null;
            return new KeysetPage<AdminUserResponse>(result, next);
        }

        private static OrganizationResponse ToOrgResponse(Organization org, int memberCount) => new()
        {
            Id = org.Id,
            Name = org.Name,
            TaxCode = org.TaxCode,
            CreatedAt = org.CreatedAt,
            MemberCount = memberCount
        };

        public async Task<IReadOnlyList<OrgMemberResponse>> ListOrgMembersAsync(
            Guid orgId, CancellationToken ct = default)
        {
            var rows = await _authDbContext.OrgMembers
                .AsNoTracking()
                .Where(m => m.OrgId == orgId)
                .Include(m => m.User)
                .OrderBy(m => m.JoinedAt)
                .ToListAsync(ct);

            return rows.Select(m => new OrgMemberResponse
            {
                UserId = m.UserId,
                Email = m.User.Email!,
                FullName = m.User.FullName,
                OrgRole = m.OrgRole.ToString(),
                JoinedAt = m.JoinedAt
            }).ToList();
        }

        // A6b: OrgAdmin đổi org_role thành viên trong org của mình (AUTH-4/AUTH-8). Không thuộc org caller
        // → OrgMemberNotFoundException (404). Bảo vệ bất biến org: hạ cấp OrgAdmin *cuối cùng* (target đang
        // OrgAdmin, role mới ≠ OrgAdmin, còn ≤1 OrgAdmin) → OrgMemberConflictException (409) — để org luôn
        // còn người quản (billing/thành viên). Đổi role persist → login sau mang org_role mới (A2).
        public async Task<OrgMemberResponse> ChangeOrgMemberRoleAsync(
            Guid orgId, Guid userId, OrgRole newRole, CancellationToken ct = default)
        {
            var member = await _authDbContext.OrgMembers
                .Include(m => m.User)
                .FirstOrDefaultAsync(m => m.OrgId == orgId && m.UserId == userId, ct);
            if (member is null)
                throw new OrgMemberNotFoundException("Member not found in this organization");

            if (member.OrgRole == OrgRole.OrgAdmin && newRole != OrgRole.OrgAdmin)
            {
                var adminCount = await _authDbContext.OrgMembers
                    .CountAsync(m => m.OrgId == orgId && m.OrgRole == OrgRole.OrgAdmin, ct);
                if (adminCount <= 1)
                    throw new OrgMemberConflictException("Cannot demote the last OrgAdmin of the organization");
            }

            member.OrgRole = newRole;
            await _authDbContext.SaveChangesAsync(ct);

            return new OrgMemberResponse
            {
                UserId = member.UserId,
                Email = member.User.Email!,
                FullName = member.User.FullName,
                OrgRole = member.OrgRole.ToString(),
                JoinedAt = member.JoinedAt
            };
        }

        // A6b: OrgAdmin xoá thành viên khỏi org của mình. Hard-remove chỉ hàng membership (org_members) —
        // account User giữ nguyên (email UNIQUE, có thể thuộc org khác / dùng lại). Không thuộc org →
        // OrgMemberNotFoundException (404); xoá OrgAdmin *cuối cùng* → OrgMemberConflictException (409).
        // Chặn "tự xoá mình" ở controller (so sánh claim sub) trước khi vào đây.
        public async Task RemoveOrgMemberAsync(Guid orgId, Guid userId, CancellationToken ct = default)
        {
            var member = await _authDbContext.OrgMembers
                .FirstOrDefaultAsync(m => m.OrgId == orgId && m.UserId == userId, ct);
            if (member is null)
                throw new OrgMemberNotFoundException("Member not found in this organization");

            if (member.OrgRole == OrgRole.OrgAdmin)
            {
                var adminCount = await _authDbContext.OrgMembers
                    .CountAsync(m => m.OrgId == orgId && m.OrgRole == OrgRole.OrgAdmin, ct);
                if (adminCount <= 1)
                    throw new OrgMemberConflictException("Cannot remove the last OrgAdmin of the organization");
            }

            _authDbContext.OrgMembers.Remove(member);
            await _authDbContext.SaveChangesAsync(ct);
        }

        private async Task EnsureRoleExistsAsync(string roleName)
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                await _roleManager.CreateAsync(new Role
                {
                    Id = Guid.NewGuid(),
                    Name = roleName,
                    NormalizedName = roleName.ToUpperInvariant()
                });
            }
        }

        // A2: 1 user thuộc ≤1 org ở phase 1 (1 org = 1 OrgAdmin) → lấy membership đầu tiên (null nếu không thuộc org)
        private Task<OrgMember?> GetMembershipAsync(Guid userId) =>
            _authDbContext.OrgMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == userId);

        private async Task<AuthResponse> GenerateAuthResponse(User user)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var membership = await GetMembershipAsync(user.Id);
            var accessToken = _jwtService.GenerateAccessToken(user, roles, membership);
            var rawRefreshToken = _jwtService.GenerateRefreshToken();

            var refreshTokenEntity = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Token = _jwtService.HashRefreshToken(rawRefreshToken),
                ExpiresAt = DateTime.UtcNow.AddDays(GetRefreshTokenDays()),
                CreatedAt = DateTime.UtcNow
            };

            _authDbContext.RefreshTokens.Add(refreshTokenEntity);
            await _authDbContext.SaveChangesAsync();

            return BuildAuthResponse(accessToken, rawRefreshToken);
        }

        private int GetRefreshTokenDays() =>
            int.Parse(_configuration["Jwt:RefreshTokenDays"]
                ?? throw new InvalidOperationException("Jwt:RefreshTokenDays is not configured"));

        private int GetAccessTokenMinutes() =>
            int.Parse(_configuration["Jwt:AccessTokenMinutes"]
                ?? throw new InvalidOperationException("Jwt:AccessTokenMinutes is not configured"));

        private AuthResponse BuildAuthResponse(string accessToken, string refreshToken) => new()
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(GetAccessTokenMinutes())
        };

        public async Task<UserResponse> GetUserAsync(Guid userId)
        {
            var user = await _authDbContext.Users.FindAsync(userId);
            if (user is null)
                throw new KeyNotFoundException("User not found");

            return new UserResponse
            {
                Id = user.Id.ToString(),
                FullName = user.FullName,
                Email = user.Email,
                Location = user.Location,
                Title = user.Title,
                CreatedAt = user.CreatedAt,
                Role = (await _userManager.GetRolesAsync(user)).FirstOrDefault() ?? "No role"
            };
        }

        public async Task<string> UpdateUserAsync(Guid userId, UpdateProfileRequest request)
        {
            var user = await _authDbContext.Users.FindAsync(userId);
            if (user is null)
                throw new KeyNotFoundException("User not found");

            user.FullName = request.FullName ?? user.FullName;
            user.Location = request.Location ?? user.Location;
            user.Title = request.Title ?? user.Title;
            user.UpdatedAt = DateTime.UtcNow;

            _authDbContext.Users.Update(user);
            await _authDbContext.SaveChangesAsync();
            return "Updated profile object";
        }

        public Task<RefreshToken> GetRefreshTokenAsync(string refreshToken)
        {
            var refreshTokenHash = _jwtService.HashRefreshToken(refreshToken);
            return _authDbContext.RefreshTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Token == refreshTokenHash);
        }
    }
}