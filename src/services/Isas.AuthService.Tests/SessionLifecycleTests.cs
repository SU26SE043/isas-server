using System.Security.Claims;
using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// Vòng đời phiên đăng nhập: cửa sổ ân hạn khi xoay vòng refresh token (đua giữa nhiều tab),
/// đăng xuất thu hồi TẤT CẢ token, đổi/gỡ quyền org thu hồi token, và mã lỗi email trùng.
///
/// Refresh token lưu dưới dạng SHA-256 hash và <see cref="JwtService.HashRefreshToken"/> tất định
/// → seed thẳng hàng token với raw biết trước, không phải chạy qua một lượt refresh thật. Nhờ vậy
/// dựng được mốc thời gian chính xác (token thay thế tạo cách đây bao lâu) mà không cần chờ đồng hồ.
/// </summary>
public class SessionLifecycleTests
{
    // ── Việc 1: cửa sổ ân hạn khi xoay vòng ────────────────────────────────────

    [Fact]
    public async Task RotatedToken_ReusedInsideGraceWindow_StillRefreshes()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "multi-tab@acme.test");

        // Tab A đã refresh 5 giây trước: T1 bị thu hồi, thay bằng T2 (còn sống).
        var (t1Raw, _) = SeedRotatedPair(db, user.Id, replacementAgeSeconds: 5);

        var svc = NewService(db, TestConfig(), MockUserManager(db));

        // Tab B đến muộn với T1 — trong cửa sổ ân hạn thì vẫn phải đổi được token, không bị đá ra.
        var resp = await svc.RefreshTokenAsync(t1Raw);

        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(resp.RefreshToken));
    }

    [Fact]
    public async Task RotatedToken_ReusedPastGraceWindow_Throws401()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "stale@acme.test");

        // Token bị xoay vòng từ 10 phút trước — quá xa cửa sổ 60s. Đây chính là ca "dùng lại token
        // đã chết" mà reuse-detection phải chặn; ân hạn KHÔNG được nới tới đây.
        var (t1Raw, _) = SeedRotatedPair(db, user.Id, replacementAgeSeconds: 600);

        var svc = NewService(db, TestConfig(), MockUserManager(db));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshTokenAsync(t1Raw));
    }

    [Fact]
    public async Task GraceWindow_DisabledByConfig_RejectsEvenFreshRotation()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "strict@acme.test");
        var (t1Raw, _) = SeedRotatedPair(db, user.Id, replacementAgeSeconds: 1);

        // Grace=0 → về đúng hành vi cũ (thu hồi tức thì, reuse-detection chặt nhất).
        var svc = NewService(db, TestConfig(graceSeconds: "0"), MockUserManager(db));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshTokenAsync(t1Raw));
    }

    [Fact]
    public async Task RevokedWithoutReplacement_GetsNoGrace_EvenImmediately()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "logged-out@acme.test");

        // Thu hồi thẳng tay (đăng xuất / đổi quyền) → KHÔNG có ReplacedBy → phải chết ngay lập tức.
        // Nếu ân hạn nới cho ca này thì "đăng xuất" sẽ vẫn refresh được thêm 60s = vô nghĩa.
        var raw = "raw-revoked-by-logout";
        db.RefreshTokens.Add(NewToken(user.Id, raw, isRevoked: true, replacedBy: null, ageSeconds: 0));
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshTokenAsync(raw));
    }

    [Fact]
    public async Task GraceRefresh_RotatesLiveHeadOfChain_NotTheStaleToken()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "chain@acme.test");
        var (t1Raw, t2Id) = SeedRotatedPair(db, user.Id, replacementAgeSeconds: 5);

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        await svc.RefreshTokenAsync(t1Raw);

        using var verify = testDb.NewContext();
        var t2 = verify.RefreshTokens.Single(x => x.Id == t2Id);

        // Token đang sống ở cuối chuỗi (T2) mới là cái bị xoay tiếp → T3 nối vào chuỗi, hai tab hội tụ.
        Assert.True(t2.IsRevoked);
        Assert.NotNull(t2.ReplacedBy);
        Assert.Equal(3, verify.RefreshTokens.Count());
    }

    [Fact]
    public async Task ValidToken_NormalRotation_StillWorks()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "happy@acme.test");
        var raw = "raw-live-token";
        db.RefreshTokens.Add(NewToken(user.Id, raw, isRevoked: false, replacedBy: null, ageSeconds: 0));
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var resp = await svc.RefreshTokenAsync(raw);

        Assert.False(string.IsNullOrWhiteSpace(resp.RefreshToken));

        using var verify = testDb.NewContext();
        var old = verify.RefreshTokens.Single(x => x.Token == Hash(raw));
        Assert.True(old.IsRevoked);
        Assert.NotNull(old.ReplacedBy);   // rotation vẫn nối chuỗi như cũ
    }

    [Fact]
    public async Task ExpiredToken_Throws401()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "expired@acme.test");
        var raw = "raw-expired";
        var token = NewToken(user.Id, raw, isRevoked: false, replacedBy: null, ageSeconds: 0);
        token.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        db.RefreshTokens.Add(token);
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshTokenAsync(raw));
    }

    [Fact]
    public async Task UnknownToken_Throws401()
    {
        using var testDb = new AuthTestDb();
        var svc = NewService(testDb.Db, TestConfig(), MockUserManager(testDb.Db));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshTokenAsync("never-issued"));
    }

    // ── Việc 3: đăng xuất thu hồi TẤT CẢ ───────────────────────────────────────

    [Fact]
    public async Task Logout_RevokesEveryRefreshTokenOfUser_NotJustOne()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "many-tabs@acme.test");
        var other = SeedUser(db, "someone-else@acme.test");

        // 3 tab = 3 refresh token còn sống của cùng user.
        db.RefreshTokens.AddRange(
            NewToken(user.Id, "tab-a", isRevoked: false, replacedBy: null, ageSeconds: 0),
            NewToken(user.Id, "tab-b", isRevoked: false, replacedBy: null, ageSeconds: 0),
            NewToken(user.Id, "tab-c", isRevoked: false, replacedBy: null, ageSeconds: 0),
            NewToken(other.Id, "other-user", isRevoked: false, replacedBy: null, ageSeconds: 0));
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        await svc.LogoutAsync(user.Id);

        using var verify = testDb.NewContext();
        Assert.All(verify.RefreshTokens.Where(x => x.UserId == user.Id), t => Assert.True(t.IsRevoked));
        // Phiên của user KHÁC không bị đụng tới.
        Assert.False(verify.RefreshTokens.Single(x => x.UserId == other.Id).IsRevoked);
    }

    [Fact]
    public async Task LogoutThenRefresh_IsRejected_NoGraceForLogout()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "logout-then-refresh@acme.test");
        db.RefreshTokens.Add(NewToken(user.Id, "still-in-a-tab", isRevoked: false, replacedBy: null, ageSeconds: 0));
        db.SaveChanges();

        await NewService(db, TestConfig(), MockUserManager(db)).LogoutAsync(user.Id);

        // Refresh đi bằng DbContext MỚI — mô phỏng đúng production: mỗi HTTP request là một scope
        // DbContext riêng. (Dùng lại context đã logout thì entity còn tracked với giá trị cũ và
        // ExecuteUpdate không cập nhật snapshot trong bộ nhớ → test sẽ nói dối.)
        using var freshCtx = testDb.NewContext();
        var svc = NewService(freshCtx, TestConfig(), MockUserManager(freshCtx));

        // Tab khác còn cầm token cũ: đăng xuất rồi thì KHÔNG được gia hạn tiếp (đây là điểm khác
        // cửa sổ ân hạn — thu hồi do đăng xuất không đặt ReplacedBy nên không hưởng ân hạn).
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshTokenAsync("still-in-a-tab"));
    }

    // ── Việc 2: đổi quyền org thu hồi refresh token ────────────────────────────

    [Fact]
    public async Task ChangeOrgRole_RevokesTargetRefreshTokens()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db);
        var admin = SeedUser(db, "admin@acme.test");
        var hr = SeedUser(db, "hr@acme.test");
        db.OrgMembers.AddRange(
            new OrgMember { OrgId = orgId, UserId = admin.Id, OrgRole = OrgRole.OrgAdmin },
            new OrgMember { OrgId = orgId, UserId = hr.Id, OrgRole = OrgRole.HrMember });
        db.RefreshTokens.AddRange(
            NewToken(hr.Id, "hr-tab", isRevoked: false, replacedBy: null, ageSeconds: 0),
            NewToken(admin.Id, "admin-tab", isRevoked: false, replacedBy: null, ageSeconds: 0));
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        await svc.ChangeOrgMemberRoleAsync(orgId, hr.Id, OrgRole.OrgAdmin);

        using var verify = testDb.NewContext();
        // Người bị đổi quyền phải lấy token mới (mang org_role mới) ở lần refresh kế.
        Assert.True(verify.RefreshTokens.Single(x => x.UserId == hr.Id).IsRevoked);
        // Người thao tác (OrgAdmin) không bị đăng xuất lây.
        Assert.False(verify.RefreshTokens.Single(x => x.UserId == admin.Id).IsRevoked);
    }

    [Fact]
    public async Task RemoveOrgMember_RevokesTargetRefreshTokens()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db);
        var admin = SeedUser(db, "admin2@acme.test");
        var hr = SeedUser(db, "hr2@acme.test");
        db.OrgMembers.AddRange(
            new OrgMember { OrgId = orgId, UserId = admin.Id, OrgRole = OrgRole.OrgAdmin },
            new OrgMember { OrgId = orgId, UserId = hr.Id, OrgRole = OrgRole.HrMember });
        db.RefreshTokens.Add(NewToken(hr.Id, "removed-hr-tab", isRevoked: false, replacedBy: null, ageSeconds: 0));
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        await svc.RemoveOrgMemberAsync(orgId, hr.Id);

        using var verify = testDb.NewContext();
        // Token cũ còn mang org_id của org vừa bị gỡ → phải chết, không sống tiếp 7 ngày.
        Assert.True(verify.RefreshTokens.Single(x => x.UserId == hr.Id).IsRevoked);
        Assert.Empty(verify.OrgMembers.Where(m => m.UserId == hr.Id));
    }

    [Fact]
    public async Task FailedRoleChange_LastOrgAdmin_DoesNotRevokeTokens()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db);
        var admin = SeedUser(db, "solo-admin@acme.test");
        db.OrgMembers.Add(new OrgMember { OrgId = orgId, UserId = admin.Id, OrgRole = OrgRole.OrgAdmin });
        db.RefreshTokens.Add(NewToken(admin.Id, "solo-tab", isRevoked: false, replacedBy: null, ageSeconds: 0));
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));

        await Assert.ThrowsAsync<OrgMemberConflictException>(
            () => svc.ChangeOrgMemberRoleAsync(orgId, admin.Id, OrgRole.HrMember));

        // Thao tác bị chặn → không được đăng xuất người ta oan.
        using var verify = testDb.NewContext();
        Assert.False(verify.RefreshTokens.Single(x => x.UserId == admin.Id).IsRevoked);
    }

    // ── Việc 4: email trùng → 409 (thống nhất với POST /auth/org/members) ───────

    [Fact]
    public async Task Register_DuplicateEmail_Returns409Conflict()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedUser(db, "taken@acme.test");

        var ctrl = NewAuthController(db);
        var result = await ctrl.RegisterAsync(new RegisterRequest
        {
            Email = "taken@acme.test",
            Password = "Passw0rd!",
            FullName = "Dup"
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        // Message giữ nguyên và nằm ở key `error` → FE rút ra hiển thị được (extractErrorMessage).
        Assert.Contains("Email already exists", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task RegisterOrg_DuplicateEmail_Returns409Conflict()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedUser(db, "orgtaken@acme.test");

        var ctrl = NewAuthController(db);
        var result = await ctrl.RegisterOrgAsync(new RegisterOrgRequest
        {
            Email = "orgtaken@acme.test",
            Password = "Passw0rd!",
            FullName = "Dup",
            OrgName = "Acme"
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string Hash(string raw) => new JwtService(TestConfig()).HashRefreshToken(raw);

    /// <summary>
    /// Dựng cặp token đã xoay vòng: T1 (thu hồi, ReplacedBy=T2) → T2 (còn sống, tạo cách đây
    /// <paramref name="replacementAgeSeconds"/> giây). CreatedAt của T2 chính là mốc "T1 bị thu hồi
    /// lúc nào" mà cửa sổ ân hạn đo theo. Trả (raw T1, id T2).
    /// </summary>
    private static (string T1Raw, Guid T2Id) SeedRotatedPair(
        AuthDbContext db, Guid userId, int replacementAgeSeconds)
    {
        var t1Raw = $"raw-t1-{Guid.NewGuid()}";
        var t2Raw = $"raw-t2-{Guid.NewGuid()}";

        var t2 = NewToken(userId, t2Raw, isRevoked: false, replacedBy: null, ageSeconds: replacementAgeSeconds);
        var t1 = NewToken(userId, t1Raw, isRevoked: true, replacedBy: t2.Id, ageSeconds: replacementAgeSeconds + 60);

        db.RefreshTokens.AddRange(t2, t1);
        db.SaveChanges();
        return (t1Raw, t2.Id);
    }

    private static RefreshToken NewToken(
        Guid userId, string raw, bool isRevoked, Guid? replacedBy, int ageSeconds) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Token = Hash(raw),
            IsRevoked = isRevoked,
            ReplacedBy = replacedBy,
            CreatedAt = DateTime.UtcNow.AddSeconds(-ageSeconds),
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

    private static Guid SeedOrg(AuthDbContext db)
    {
        var org = new Organization { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTime.UtcNow };
        db.Organizations.Add(org);
        db.SaveChanges();
        return org.Id;
    }

    private static User SeedUser(AuthDbContext db, string email)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            FullName = email,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static Isas.AuthService.Services.AuthService NewService(
        AuthDbContext db, IConfiguration config, Mock<UserManager<User>> userManager)
    {
        var roleManager = new Mock<RoleManager<Role>>(Mock.Of<IRoleStore<Role>>(), null!, null!, null!, null!);
        roleManager.Setup(m => m.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        var signInManager = new Mock<SignInManager<User>>(userManager.Object, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);
        return new Isas.AuthService.Services.AuthService(
            db, new JwtService(config), userManager.Object, roleManager.Object, config, signInManager.Object);
    }

    /// <summary>AuthController thật + AuthDbContext SQLite; chỉ cần UserManager.FindByEmailAsync cho ca trùng email.</summary>
    private static AuthController NewAuthController(AuthDbContext db)
    {
        var config = TestConfig();
        var userManager = MockUserManager(db);
        var signInManager = new Mock<SignInManager<User>>(userManager.Object, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);

        return new AuthController(
            NewService(db, config, userManager),
            userManager.Object,
            signInManager.Object,
            Mock.Of<IEmailSender>(),
            Mock.Of<IGoogleLoginRedirects>(),
            Mock.Of<ILogger<AuthController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static IConfiguration TestConfig(string graceSeconds = "60") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "isas-test-signing-key-0123456789-abcdef",
            ["Jwt:Issuer"] = "isas-test",
            ["Jwt:Audience"] = "isas-test",
            ["Jwt:AccessTokenMinutes"] = "15",
            ["Jwt:RefreshTokenDays"] = "7",
            ["Jwt:RefreshTokenGraceSeconds"] = graceSeconds
        }).Build();

    private static Mock<UserManager<User>> MockUserManager(AuthDbContext db)
    {
        var mgr = new Mock<UserManager<User>>(
            Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);

        mgr.Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .Returns<string>(e => Task.FromResult(
                db.Users.FirstOrDefault(u => u.Email!.ToLower() == e.ToLower())));
        mgr.Setup(m => m.GetRolesAsync(It.IsAny<User>()))
            .ReturnsAsync(new List<string> { "Candidate" });
        return mgr;
    }
}
