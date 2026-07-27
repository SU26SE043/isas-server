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

            return await GenerateAuthResponse(user, LoginMethod.Password);
        }

        // AUTH-1: đăng nhập Google → account Candidate (không mở đường Employer/org ở đây).
        // Ba nhánh: (1) external login đã liên kết → dùng lại; (2) email trùng account SẴN CÓ →
        // LIÊN KẾT vào account đó; (3) chưa có gì → tạo mới passwordless.
        public async Task<AuthResponse> LoginGoogleAsync(ExternalLoginInfo info)
        {
            // Không dùng SignInManager.ExternalLoginSignInAsync: khi tìm thấy user nó gọi
            // Context.SignInAsync(IdentityConstants.ApplicationScheme) để ghi cookie phiên — service
            // này chỉ AddIdentityCore nên scheme đó KHÔNG tồn tại → ném lỗi ngay ở lần đăng nhập
            // Google THỨ HAI trở đi. API stateless (JWT) không cần cookie phiên; tra bảng
            // user_logins là đủ để biết external login đang gắn account nào.
            var linkedUser = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (linkedUser is not null)
                return await GenerateAuthResponse(linkedUser, LoginMethod.Google);

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrWhiteSpace(email))
                throw new InvalidOperationException("Google account does not provide an email");

            // Account linking: email Google trùng account mật khẩu sẵn có → gắn external login vào
            // account ĐÓ rồi đăng nhập, KHÔNG tạo user thứ hai. Trước đây rơi thẳng xuống CreateAsync
            // → vi phạm UNIQUE email (RequireUniqueEmail) → ném exception → 500, và người dùng đã
            // đăng ký bằng mật khẩu thì vĩnh viễn không đăng nhập Google được.
            var existingUser = await _userManager.FindByEmailAsync(email);
            if (existingUser is not null)
            {
                var linkResult = await _userManager.AddLoginAsync(existingUser, info);
                if (!linkResult.Succeeded)
                    throw new InvalidOperationException(string.Join("; ", linkResult.Errors.Select(e => e.Description)));

                return await GenerateAuthResponse(existingUser, LoginMethod.Google);
            }

            var newUser = new User
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                FullName = info.Principal.Identity?.Name ?? email,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await EnsureRoleExistsAsync("Candidate");   // ngoài transaction — xem ghi chú ở hàm đó

            // user + external login + role là MỘT đơn vị. Riêng đường này còn một kiểu hỏng nữa:
            // user tạo xong mà user_logins chưa ghi thì lần đăng nhập Google KẾ TIẾP không tra ra
            // liên kết, rơi xuống nhánh "email đã tồn tại" và người dùng kẹt ở một account không role.
            await RunInTransactionAsync(async () =>
            {
                // Passwordless (mẫu ProvisionCandidate) — user Google-only đặt mật khẩu qua forgot/reset.
                var identityResult = await _userManager.CreateAsync(newUser);
                if (!identityResult.Succeeded)
                    throw new InvalidOperationException(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

                var addLoginResult = await _userManager.AddLoginAsync(newUser, info);
                if (!addLoginResult.Succeeded)
                    throw new InvalidOperationException(string.Join("; ", addLoginResult.Errors.Select(e => e.Description)));

                var roleResult = await _userManager.AddToRoleAsync(newUser, "Candidate");
                if (!roleResult.Succeeded)
                    throw new InvalidOperationException(string.Join("; ", roleResult.Errors.Select(e => e.Description)));
            });

            return await GenerateAuthResponse(newUser, LoginMethod.Google);
        }

        // Đăng xuất = thu hồi MỌI refresh token của user, không chỉ token client gửi lên.
        // VÌ SAO: mỗi tab giữ refresh token riêng nhưng dùng chung một phiên. Thu hồi đúng 1 token thì
        // tab khác vẫn gia hạn phiên tiếp → "đã đăng xuất" mà phiên vẫn sống. Đăng xuất là đăng xuất.
        // GIỚI HẠN (có chủ đích): access token ĐANG lưu hành KHÔNG thu hồi được — các service validate
        // JWT offline bằng chung key, không hỏi AuthService lúc chạy (GEN-3) → token cũ còn hợp lệ tới
        // hết TTL (15'). Vì vậy FE PHẢI tự xoá token khỏi storage khi đăng xuất. Xem docs/services/auth.md.
        public Task LogoutAsync(Guid userId) => RevokeAllRefreshTokensAsync(userId);

        public async Task<AuthResponse> RefreshTokenAsync(string refreshToken)
        {
            var hashedToken = _jwtService.HashRefreshToken(refreshToken);

            // Tra KHÔNG lọc IsRevoked: token đã bị xoay vòng vẫn còn cơ hội đi đường ân hạn bên dưới.
            var presented = await _authDbContext.RefreshTokens
                .FirstOrDefaultAsync(x => x.Token == hashedToken);

            if (presented is null)
                throw new UnauthorizedAccessException("Invalid refresh token");

            // Token đã bị xoay vòng → chỉ chấp nhận trong cửa sổ ân hạn, và xoay tiếp từ token ĐANG SỐNG
            // ở cuối chuỗi ReplacedBy (tab kia có thể đã refresh vài lần).
            var tokenToRotate = presented.IsRevoked
                ? await ResolveGraceReplacementAsync(presented)
                : presented;

            if (tokenToRotate is null)
                throw new UnauthorizedAccessException("Invalid refresh token");

            if (tokenToRotate.ExpiresAt < DateTime.UtcNow)
                throw new UnauthorizedAccessException("Refresh token expired");

            var user = await _authDbContext.Users
                .FirstOrDefaultAsync(u => u.Id == tokenToRotate.UserId)
                ?? throw new UnauthorizedAccessException("Invalid refresh token");

            // F20 — chặn refresh của account bị đình chỉ. Ban đã thu hồi mọi refresh token nên đường
            // này thường không tới được; kiểm ở đây bịt ĐUA: một refresh đang bay song song với lệnh
            // ban có thể chèn token mới SAU khi ExecuteUpdate quét xong → phiên sống tiếp vô thời hạn.
            EnsureNotBanned(user);

            tokenToRotate.IsRevoked = true;

            var newRawRefreshToken = _jwtService.GenerateRefreshToken();
            var newRefreshTokenEntity = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = tokenToRotate.UserId,
                Token = _jwtService.HashRefreshToken(newRawRefreshToken),
                ExpiresAt = DateTime.UtcNow.AddDays(GetRefreshTokenDays()),
                CreatedAt = DateTime.UtcNow
            };

            tokenToRotate.ReplacedBy = newRefreshTokenEntity.Id;
            _authDbContext.RefreshTokens.Add(newRefreshTokenEntity);
            await _authDbContext.SaveChangesAsync();

            // Đọc lại roles + membership mỗi lần refresh → quyền mới vào token ở lần refresh kế
            // (độ trễ tối đa = TTL access token). Đây là ranh giới hiệu lực của việc đổi quyền.
            var roles = await _userManager.GetRolesAsync(user);
            var membership = await GetMembershipAsync(tokenToRotate.UserId);
            var accessToken = _jwtService.GenerateAccessToken(user, roles, membership);

            return BuildAuthResponse(accessToken, newRawRefreshToken);
        }

        // Số bước tối đa đi theo chuỗi ReplacedBy — chặn vòng lặp vô hạn nếu dữ liệu chuỗi hỏng.
        private const int MaxGraceChainHops = 8;

        /// <summary>
        /// Cửa sổ ân hạn khi xoay vòng refresh token — sửa đua refresh giữa NHIỀU TAB.
        ///
        /// VẤN ĐỀ: mỗi tab giữ refresh token trong bộ nhớ riêng (đọc lúc tải trang) nhưng dùng chung
        /// storage. Tab A refresh → token cũ bị thu hồi TỨC THÌ → tab B (còn cầm token cũ) refresh sau
        /// đó → 401 → người dùng bị đá về trang đăng nhập dù phiên vẫn còn hạn. Mở 2 tab là dính; mua
        /// credit qua PayOS gần như luôn tạo tab thứ hai.
        ///
        /// ⚠ ĐÁNH ĐỔI BẢO MẬT (có chủ đích, không phải sơ suất): thu-hồi-tức-thì chính là cơ chế
        /// PHÁT HIỆN TOKEN BỊ ĐÁNH CẮP (reuse detection) — một refresh token dùng hai lần là dấu hiệu
        /// bị trộm. Ân hạn làm YẾU cơ chế đó bên trong đúng cửa sổ này: kẻ trộm dùng lại token trong
        /// vòng &lt;grace&gt; giây sẽ không bị chặn. Vì vậy giữ cửa sổ NGẮN (mặc định 60s) và vẫn trả 401
        /// khi đã quá hạn ân hạn → thiệt hại giới hạn ở vài chục giây, đổi lấy việc người dùng thật
        /// không bị đăng xuất oan khi mở nhiều tab.
        ///
        /// Token bị thu hồi mà KHÔNG có ReplacedBy (đăng xuất, đổi quyền) → KHÔNG ân hạn, chết ngay.
        /// </summary>
        private async Task<RefreshToken?> ResolveGraceReplacementAsync(RefreshToken revoked)
        {
            // Thu hồi thẳng tay (logout / đổi org-role / xoá khỏi org) không đặt ReplacedBy → phải chết
            // ngay lập tức, không được hưởng ân hạn. Chỉ token bị XOAY VÒNG mới có ReplacedBy.
            if (revoked.ReplacedBy is not Guid replacementId)
                return null;

            var replacement = await _authDbContext.RefreshTokens
                .FirstOrDefaultAsync(x => x.Id == replacementId);
            if (replacement is null)
                return null;

            // Mốc "bị thu hồi lúc nào" lấy từ CreatedAt của token thay thế — token thay thế được tạo
            // đúng thời điểm token cũ bị thu hồi (cùng một SaveChanges) → không cần thêm cột
            // revoked_at, tức là KHÔNG cần migration (schema DB server không phải đụng tới).
            if (DateTime.UtcNow - replacement.CreatedAt > TimeSpan.FromSeconds(GetRefreshTokenGraceSeconds()))
                return null;

            // Trong cửa sổ ân hạn: đi tới token còn sống ở cuối chuỗi. KHÔNG trả lại chính token thay
            // thế cho client được vì DB chỉ lưu HASH (raw token chỉ tồn tại ở máy client) → cấp cặp
            // token mới cho tab đến muộn là cách duy nhất, và hai tab hội tụ qua đồng bộ storage ở FE.
            var current = replacement;
            for (var hop = 0; current.IsRevoked && hop < MaxGraceChainHops; hop++)
            {
                if (current.ReplacedBy is not Guid nextId)
                    return null;

                var next = await _authDbContext.RefreshTokens
                    .FirstOrDefaultAsync(x => x.Id == nextId);
                if (next is null)
                    return null;

                current = next;
            }

            return current.IsRevoked ? null : current;
        }

        // Thu hồi mọi refresh token còn sống của 1 user. Dùng cho đăng xuất và cho các thay đổi quyền
        // (đổi org-role / xoá khỏi org) — buộc phát lại token ở lần refresh kế thay vì để quyền cũ
        // sống hết 7 ngày của refresh token. KHÔNG đặt ReplacedBy → không hưởng cửa sổ ân hạn.
        //
        // ⚠ KHÔNG dựng denylist access-token: các service khác validate JWT OFFLINE bằng chung key
        // (GEN-3, ràng buộc cứng trong AGENTS.md). Thêm bước gọi mạng/Redis vào đường validate là vi
        // phạm ràng buộc đó. Hệ quả chấp nhận: quyền cũ còn hiệu lực tối đa 1 TTL access token (15').
        private Task RevokeAllRefreshTokensAsync(Guid userId, CancellationToken ct = default) =>
            _authDbContext.RefreshTokens
                .Where(x => x.UserId == userId && !x.IsRevoked)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true), ct);

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

            await EnsureRoleExistsAsync("Candidate");   // ngoài transaction — xem ghi chú ở hàm đó

            // user + role là MỘT đơn vị: user không có role thì đăng nhập được nhưng mọi endpoint
            // [Authorize(Roles)] trả 403, và không có gì dọn.
            await RunInTransactionAsync(async () =>
            {
                var result = await _userManager.CreateAsync(user, registerRequest.Password);
                if (!result.Succeeded)
                    throw new Exception(string.Join("; ", result.Errors.Select(e => e.Description)));

                var roleResult = await _userManager.AddToRoleAsync(user, "Candidate");
                if (!roleResult.Succeeded)
                    throw new Exception(string.Join("; ", roleResult.Errors.Select(e => e.Description)));
            });

            // NGOÀI transaction, có chủ đích: refresh token là trạng thái PHIÊN, không phải trạng
            // thái tài khoản. Hỏng ở đây để lại một tài khoản đầy đủ và hợp lệ — người dùng chỉ cần
            // đăng nhập lại. Kéo vào trong sẽ đánh đổi ngược: giữ transaction mở lâu hơn để bảo vệ
            // thứ tự phục hồi được bằng một lần bấm "Đăng nhập".
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

            await EnsureRoleExistsAsync("Employer");   // ngoài transaction — xem ghi chú ở hàm đó

            // user + role + org + membership là MỘT đơn vị: dở dang ở đây để lại Employer không thuộc
            // org nào, mà org_id quyết định cả quyền lẫn billing (AUTH-8).
            await RunInTransactionAsync(async () =>
            {
                var result = await _userManager.CreateAsync(user, request.Password);
                if (!result.Succeeded)
                    throw new Exception(string.Join("; ", result.Errors.Select(e => e.Description)));

                var roleResult = await _userManager.AddToRoleAsync(user, "Employer");
                if (!roleResult.Succeeded)
                    throw new Exception(string.Join("; ", roleResult.Errors.Select(e => e.Description)));

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
            });

            // Sau COMMIT: GenerateAuthResponse đọc lại membership để nhét org_id/org_role vào token
            // (A2) — chỉ đúng khi membership đã thật sự nằm trong DB.
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

                await EnsureRoleExistsAsync("Candidate");   // ngoài transaction — xem ghi chú ở hàm đó

                await RunInTransactionAsync(async () =>
                {
                    var result = await _userManager.CreateAsync(user);   // KHÔNG mật khẩu (magic-link)
                    if (!result.Succeeded)
                        throw new Exception(string.Join("; ", result.Errors.Select(e => e.Description)));

                    var roleResult = await _userManager.AddToRoleAsync(user, "Candidate");
                    if (!roleResult.Succeeded)
                        throw new Exception(string.Join("; ", roleResult.Errors.Select(e => e.Description)));
                }, ct);
            }

            // F20 — account đã bị đình chỉ thì magic-link B2B KHÔNG được cấp JWT: đường này phát token
            // chỉ dựa trên EMAIL (không mật khẩu), nên bỏ sót ở đây là mở nguyên một cửa vòng qua ban.
            EnsureNotBanned(user);

            var roles = await _userManager.GetRolesAsync(user);
            var membership = await GetMembershipAsync(user.Id);
            var accessToken = _jwtService.GenerateAccessToken(user, roles, membership);

            await RecordLoginBestEffortAsync(user.Id, LoginMethod.MagicLink, ct);

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

            await EnsureRoleExistsAsync("Employer");   // ngoài transaction — xem ghi chú ở hàm đó

            var member = new OrgMember
            {
                OrgId = orgId,
                UserId = user.Id,
                OrgRole = OrgRole.HrMember,
                JoinedAt = DateTime.UtcNow
            };

            // user + role + membership là MỘT đơn vị: HrMember không có membership thì không thuộc org
            // nào, và vì email đã UNIQUE nên OrgAdmin KHÔNG mời lại được cùng email đó để tự sửa.
            await RunInTransactionAsync(async () =>
            {
                var result = await _userManager.CreateAsync(user);   // KHÔNG mật khẩu (đặt qua forgot/reset)
                if (!result.Succeeded)
                    throw new Exception(string.Join("; ", result.Errors.Select(e => e.Description)));

                var roleResult = await _userManager.AddToRoleAsync(user, "Employer");
                if (!roleResult.Succeeded)
                    throw new Exception(string.Join("; ", roleResult.Errors.Select(e => e.Description)));

                _authDbContext.OrgMembers.Add(member);
                await _authDbContext.SaveChangesAsync(ct);
            }, ct);

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
            ValidateAdminPageInput(cursor, limit);
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
            ValidateAdminPageInput(cursor, limit);
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
                    CreatedAt = u.CreatedAt,
                    // F20 — trạng thái đình chỉ hiện ngay trong danh sách: admin phải thấy ai đang bị
                    // cấm trước khi bấm nút, và FE cần dữ liệu này để render nút Ban/Gỡ ban cho đúng.
                    BannedAt = u.BannedAt,
                    BanReason = u.BanReason
                });
            }

            var next = users.Count == take
                ? new KeysetCursor(users[^1].CreatedAt, users[^1].Id).Encode()
                : null;
            return new KeysetPage<AdminUserResponse>(result, next);
        }

        private static void ValidateAdminPageInput(string? cursor, int? limit)
        {
            if (limit is <= 0)
                throw new ArgumentException("limit must be greater than 0.");
            if (!KeysetCursor.TryValidate(cursor))
                throw new ArgumentException("cursor is malformed.");
        }

        // ── F20 (FR16) — PlatformAdmin mutation trên account người dùng ────────────────────────
        //
        // ⚠ RANH GIỚI HIỆU LỰC CỦA BAN (AUTH-5 / GEN-3 — đọc kỹ trước khi "sửa cho chặt hơn"):
        // các service khác validate JWT OFFLINE bằng chung khoá, KHÔNG hỏi AuthService lúc chạy.
        // Vì vậy ban KHÔNG giết được access token ĐANG lưu hành: người vừa bị cấm vẫn gọi API được
        // tối đa 1 TTL access token (15'). Thứ ban làm ngay lập tức là: (1) chặn mọi đường PHÁT
        // phiên mới, (2) thu hồi mọi refresh token → không gia hạn được nữa. Sau ≤15' account chết
        // hẳn. Muốn chặt hơn thì RÚT NGẮN TTL access — TUYỆT ĐỐI KHÔNG thêm denylist/gọi mạng vào
        // đường validate của service khác (vi phạm GEN-3, ràng buộc cứng). Xem docs/services/auth.md.
        public async Task<AdminUserResponse> BanUserAsync(
            Guid actingAdminId, Guid userId, string? reason, CancellationToken ct = default)
        {
            var user = await _authDbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                ?? throw new KeyNotFoundException("User not found");

            // Bất biến: luôn còn ≥1 Admin hoạt động. Cấm hết Admin thì không còn ai GỠ ban được cho
            // ai — hệ thống tự khoá mình vĩnh viễn (chỉ sửa được bằng tay trong DB).
            if (user.BannedAt is null && await IsLastActiveAdminAsync(userId, ct))
                throw new AdminActionConflictException("Cannot ban the last active platform Admin");

            user.BannedAt = DateTime.UtcNow;
            user.BanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            user.BannedBy = actingAdminId;
            user.UpdatedAt = DateTime.UtcNow;
            await _authDbContext.SaveChangesAsync(ct);

            // Thu hồi refresh token NGAY: nếu không, người bị cấm cứ 15' gia hạn một lần và phiên
            // sống thêm trọn 7 ngày của refresh token — ban sẽ chỉ là cái nhãn trong DB.
            await RevokeAllRefreshTokensAsync(userId, ct);

            return await ToAdminUserResponseAsync(user, ct);
        }

        public async Task<AdminUserResponse> UnbanUserAsync(Guid userId, CancellationToken ct = default)
        {
            var user = await _authDbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                ?? throw new KeyNotFoundException("User not found");

            user.BannedAt = null;
            user.BanReason = null;
            user.BannedBy = null;
            user.UpdatedAt = DateTime.UtcNow;
            await _authDbContext.SaveChangesAsync(ct);

            // KHÔNG khôi phục refresh token đã thu hồi (đằng nào cũng chỉ còn hash) — người dùng
            // đăng nhập lại là có phiên mới.
            return await ToAdminUserResponseAsync(user, ct);
        }

        // F20 — Admin đặt lại mật khẩu HỘ user (user mất quyền truy cập email / cần khoá tài khoản
        // đang bị chiếm). Dùng reset-token của Identity thay vì ghi thẳng hash: qua đúng validator
        // mật khẩu + đổi SecurityStamp như đường tự-reset.
        public async Task AdminResetPasswordAsync(
            Guid userId, string newPassword, CancellationToken ct = default)
        {
            var user = await _authDbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                ?? throw new KeyNotFoundException("User not found");

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
            if (!result.Succeeded)
                throw new ArgumentException(string.Join("; ", result.Errors.Select(e => e.Description)));

            // BẮT BUỘC: đổi mật khẩu mà không thu hồi refresh token thì phiên của kẻ đang chiếm tài
            // khoản vẫn tự gia hạn thêm 7 ngày — tức là "đổi mật khẩu để cứu account" không cứu gì cả.
            await RevokeAllRefreshTokensAsync(userId, ct);
        }

        // Đếm Admin CÒN HOẠT ĐỘNG (chưa bị ban) khác người đang bị thao tác. Đọc thẳng DB (không qua
        // UserManager.GetUsersInRoleAsync) để đếm được cả cờ ban trong cùng một truy vấn.
        private async Task<bool> IsLastActiveAdminAsync(Guid userId, CancellationToken ct)
        {
            var adminRoleId = await _authDbContext.Roles
                .Where(r => r.NormalizedName == "ADMIN")
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(ct);
            if (adminRoleId is not Guid rid) return false;

            var targetIsAdmin = await _authDbContext.UserRoles
                .AnyAsync(ur => ur.UserId == userId && ur.RoleId == rid, ct);
            if (!targetIsAdmin) return false;

            var otherActiveAdmins = await _authDbContext.UserRoles
                .CountAsync(ur => ur.RoleId == rid
                                  && ur.UserId != userId
                                  && _authDbContext.Users.Any(u => u.Id == ur.UserId && u.BannedAt == null), ct);
            return otherActiveAdmins == 0;
        }

        private async Task<AdminUserResponse> ToAdminUserResponseAsync(User user, CancellationToken ct)
        {
            var membership = await _authDbContext.OrgMembers.AsNoTracking()
                .Include(m => m.Organization)
                .FirstOrDefaultAsync(m => m.UserId == user.Id, ct);

            return new AdminUserResponse
            {
                Id = user.Id,
                Email = user.Email,
                FullName = user.FullName,
                Role = (await _userManager.GetRolesAsync(user)).FirstOrDefault() ?? "No role",
                OrgId = membership?.OrgId,
                OrgName = membership?.Organization?.Name,
                OrgRole = membership?.OrgRole.ToString(),
                CreatedAt = user.CreatedAt,
                BannedAt = user.BannedAt,
                BanReason = user.BanReason
            };
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

            // Đổi quyền → thu hồi mọi refresh token của người bị đổi: lần refresh kế họ phải đăng nhập
            // lại và nhận token mang org_role MỚI, thay vì mang quyền cũ suốt 7 ngày. Ranh giới hiệu lực
            // vì thế là ≤ TTL access token (15'), tường minh và có chủ đích — xem docs/services/auth.md.
            await RevokeAllRefreshTokensAsync(userId, ct);

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

            // Gỡ khỏi org → thu hồi refresh token: token cũ còn mang org_id/org_role của org vừa rời,
            // giữ nguyên thì người đã bị gỡ vẫn thao tác được trên org đó tới 7 ngày. (Xem ghi chú
            // RevokeAllRefreshTokensAsync về việc vì sao KHÔNG denylist access token — GEN-3.)
            await RevokeAllRefreshTokensAsync(userId, ct);
        }

        /// <summary>
        /// Chạy <paramref name="work"/> trong MỘT transaction: hoặc mọi lệnh ghi bên trong đều vào DB,
        /// hoặc KHÔNG cái nào. Dùng cho các đường TẠO TÀI KHOẢN, vốn ghi nhiều lần rời rạc
        /// (users → user_roles → org/org_members) và lỗi giữa chừng để lại tài khoản dở dang mà
        /// KHÔNG có gì dọn: user không role thì đăng nhập được nhưng mọi endpoint
        /// <c>[Authorize(Roles)]</c> trả 403; Employer không thuộc org nào thì mất cả quyền lẫn
        /// billing (AUTH-8).
        ///
        /// ⚠ PHẢI đi qua <see cref="IExecutionStrategy"/>: Program.cs bật
        /// <c>EnableRetryOnFailure()</c> trên Npgsql, mà chiến lược retry TỪ CHỐI transaction do
        /// người dùng tự mở — gọi thẳng <c>BeginTransactionAsync</c> sẽ ném lúc CHẠY THẬT trong khi
        /// test (SQLite, chiến lược không-retry) vẫn xanh. Kiểu bug DB25b, chỉ nổ trên Postgres.
        ///
        /// ⚠ Đánh đổi đã biết của mẫu này: khi chiến lược retry thật sự chạy lại delegate, EF KHÔNG
        /// reset trạng thái tracking của DbContext. Chấp nhận vì đây là mẫu chuẩn EF khuyến nghị và
        /// vẫn tốt hơn hiện trạng (không transaction ⇒ tài khoản dở dang là CHẮC CHẮN, không phải
        /// chỉ khi retry).
        /// </summary>
        private Task RunInTransactionAsync(Func<Task> work, CancellationToken ct = default)
        {
            var strategy = _authDbContext.Database.CreateExecutionStrategy();
            return strategy.ExecuteAsync(async () =>
            {
                // Dispose khi chưa Commit = rollback → không cần Rollback tường minh ở nhánh lỗi.
                await using var tx = await _authDbContext.Database.BeginTransactionAsync(ct);
                await work();
                await tx.CommitAsync(ct);
            });
        }

        /// <summary>
        /// Tạo role lazy nếu chưa có.
        ///
        /// ⚠ CỐ Ý gọi NGOÀI <see cref="RunInTransactionAsync"/>. Role là DỮ LIỆU THAM CHIẾU
        /// (Candidate/Employer — hệ thống luôn cần chúng tồn tại), không phải dữ liệu của riêng một
        /// tài khoản. Kéo vào trong transaction thì hai request đăng ký ĐẦU TIÊN chạy song song sẽ
        /// đua nhau tạo cùng một role, bên thua đụng UNIQUE <c>RoleNameIndex</c>
        /// (<c>roles.normalized_name</c>) và rollback theo cả việc TẠO USER — tức là một lần đăng ký
        /// hoàn toàn hợp lệ bị 500 vì lý do chẳng liên quan gì tới nó. Để ngoài thì trường hợp xấu
        /// nhất chỉ là một row role bị bỏ lại sau một lần đăng ký hỏng, mà row đó vốn dĩ phải có.
        ///
        /// Bên thua đua được xử lý là THÀNH CÔNG: mục tiêu "role tồn tại" đã đạt, ai tạo không quan trọng.
        /// </summary>
        private async Task EnsureRoleExistsAsync(string roleName)
        {
            if (await _roleManager.RoleExistsAsync(roleName))
                return;

            var role = new Role
            {
                Id = Guid.NewGuid(),
                Name = roleName,
                NormalizedName = roleName.ToUpperInvariant()
            };

            try
            {
                var result = await _roleManager.CreateAsync(role);
                if (result.Succeeded)
                    return;
            }
            catch (DbUpdateException)
            {
                // Đụng UNIQUE vì request khác vừa tạo xong → kiểm lại bên dưới.
            }

            // Row Added hỏng còn kẹt trong change tracker sẽ được SaveChanges KẾ TIẾP (nằm trong
            // transaction tạo user) thử insert lại và làm hỏng lây transaction đó → gỡ ra.
            _authDbContext.Entry(role).State = EntityState.Detached;

            if (!await _roleManager.RoleExistsAsync(roleName))
                throw new InvalidOperationException($"Không tạo được role '{roleName}'.");
        }

        // A2: 1 user thuộc ≤1 org ở phase 1 (1 org = 1 OrgAdmin) → lấy membership đầu tiên (null nếu không thuộc org)
        private Task<OrgMember?> GetMembershipAsync(Guid userId) =>
            _authDbContext.OrgMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == userId);

        /// <summary>
        /// F20 (FR16) — cửa DUY NHẤT chặn account bị đình chỉ, đặt ngay trước khi phát token.
        ///
        /// VÌ SAO đặt ở tầng service chứ không ở controller đăng nhập: có BỐN đường phát phiên
        /// (đăng nhập mật khẩu · đăng nhập Google · refresh · <c>ProvisionCandidateAsync</c> của D2)
        /// và chỉ có đường đầu tiên đi qua kiểm mật khẩu. Chặn ở controller <c>Login</c> thì người bị
        /// cấm vẫn vào lại được bằng Google, hoặc bằng magic-link B2B (provision cấp JWT theo EMAIL,
        /// không hỏi mật khẩu). Gác ở <see cref="GenerateAuthResponse"/> + refresh + provision là
        /// phủ hết cả bốn.
        /// </summary>
        private static void EnsureNotBanned(User user)
        {
            if (user.BannedAt is not null)
                throw new UserBannedException("Account has been suspended");
        }

        private async Task<AuthResponse> GenerateAuthResponse(User user, string? loginMethod = null)
        {
            EnsureNotBanned(user);

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

            if (loginMethod is not null)
                await RecordLoginBestEffortAsync(user.Id, loginMethod);

            return BuildAuthResponse(accessToken, rawRefreshToken);
        }

        // FR18: thống kê không được phép làm hỏng đường login. Nếu SaveChanges lỗi, tách entity lỗi
        // khỏi tracker để request sau vẫn dùng được DbContext này; caller vẫn nhận được JWT hợp lệ.
        private async Task RecordLoginBestEffortAsync(Guid userId, string method, CancellationToken ct = default)
        {
            var loginEvent = new LoginEvent
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Method = method,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                _authDbContext.LoginEvents.Add(loginEvent);
                await _authDbContext.SaveChangesAsync(ct);
            }
            catch (Exception)
            {
                _authDbContext.Entry(loginEvent).State = EntityState.Detached;
            }
        }

        private int GetRefreshTokenDays() =>
            int.Parse(_configuration["Jwt:RefreshTokenDays"]
                ?? throw new InvalidOperationException("Jwt:RefreshTokenDays is not configured"));

        // Cửa sổ ân hạn xoay vòng refresh token (giây). Mặc định 60s khi không cấu hình / cấu hình rác —
        // đủ để các tab hội tụ, đủ ngắn để reuse-detection còn ý nghĩa. Đặt 0 = tắt ân hạn (chặt nhất).
        private int GetRefreshTokenGraceSeconds() =>
            int.TryParse(_configuration["Jwt:RefreshTokenGraceSeconds"], out var seconds) && seconds >= 0
                ? seconds
                : 60;

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
