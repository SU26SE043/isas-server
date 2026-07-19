using System.Reflection;
using System.Security.Claims;
using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// F20 (FR16) — PlatformAdmin đình chỉ account + đặt lại mật khẩu hộ.
///
/// Trọng tâm là ĐƯỜNG PHÁT PHIÊN, không phải cột trong DB: một lệnh ban chỉ có nghĩa nếu nó chặn
/// được CẢ BỐN cửa vào (mật khẩu · Google · refresh · provision magic-link D2). Vì thế mỗi cửa có
/// một test riêng — đặt cờ vào DB rồi chỉ assert cờ đó thì test vẫn xanh khi ban hoàn toàn vô dụng.
/// </summary>
public class AdminUserModerationTests
{
    // ── BAN chặn phát phiên: cả 4 cửa ────────────────────────────────────────────────────────
    [Fact]
    public async Task BannedUser_PasswordLogin_IsRejected()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "banned@acme.test", banned: true);
        var sut = NewService(db, TestConfig(), MockUserManager(db));

        await Assert.ThrowsAsync<UserBannedException>(
            () => sut.LoginAsync(new LoginRequest { Email = user.Email!, Password = "whatever" }));

        // Không phiên nào được phát ra.
        using var verify = testDb.NewContext();
        Assert.Empty(verify.RefreshTokens);
    }

    [Fact]
    public async Task BannedUser_GoogleLogin_IsRejected()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "banned@acme.test", banned: true);
        var userManager = MockUserManager(db);
        userManager.Setup(m => m.FindByLoginAsync("Google", "google-sub-1")).ReturnsAsync(user);
        var sut = NewService(db, TestConfig(), userManager);

        // Đăng nhập Google KHÔNG đi qua controller Login → nếu chỉ chặn ở đó thì đây là đường vòng.
        await Assert.ThrowsAsync<UserBannedException>(
            () => sut.LoginGoogleAsync(ExternalInfo("banned@acme.test", "google-sub-1")));

        using var verify = testDb.NewContext();
        Assert.Empty(verify.RefreshTokens);
    }

    [Fact]
    public async Task BannedUser_ProvisionCandidate_IsRejected_NoJwtIssued()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedUser(db, "banned@acme.test", banned: true);
        var sut = NewService(db, TestConfig(), MockUserManager(db));

        // D2 magic-link: cấp JWT chỉ dựa trên EMAIL, không hỏi mật khẩu → cửa dễ bỏ sót nhất.
        await Assert.ThrowsAsync<UserBannedException>(
            () => sut.ProvisionCandidateAsync("banned@acme.test", "Banned"));
    }

    [Fact]
    public async Task BannedUser_RefreshToken_IsRejected()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();
        var user = SeedUser(db, "banned@acme.test", banned: true);

        // Mô phỏng ĐUA: một refresh token còn sống lọt vào SAU khi lệnh ban đã quét thu hồi.
        var jwt = new JwtService(config);
        var raw = jwt.GenerateRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = jwt.HashRefreshToken(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var sut = NewService(db, config, MockUserManager(db));
        await Assert.ThrowsAsync<UserBannedException>(() => sut.RefreshTokenAsync(raw));
    }

    [Fact]
    public async Task ActiveUser_CanStillLogin_BanDoesNotBlockEveryone()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = SeedUser(db, "ok@acme.test");
        var sut = NewService(db, TestConfig(), MockUserManager(db));

        var resp = await sut.LoginAsync(new LoginRequest { Email = user.Email!, Password = "x" });

        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));
        using var verify = testDb.NewContext();
        Assert.Single(verify.RefreshTokens);
    }

    // ── BAN thu hồi phiên đang sống ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Ban_RevokesAllRefreshTokens_OfThatUserOnly()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var target = SeedUser(db, "target@acme.test");
        var bystander = SeedUser(db, "other@acme.test");
        SeedRefreshToken(db, target.Id, "hash-a");
        SeedRefreshToken(db, target.Id, "hash-b");
        SeedRefreshToken(db, bystander.Id, "hash-c");
        var admin = SeedUser(db, "admin@acme.test");

        var sut = NewService(db, TestConfig(), MockUserManager(db));
        await sut.BanUserAsync(admin.Id, target.Id, "spam");

        using var verify = testDb.NewContext();
        // Không thu hồi thì người bị cấm cứ 15' gia hạn một lần → phiên sống thêm trọn 7 ngày.
        Assert.All(verify.RefreshTokens.Where(t => t.UserId == target.Id), t => Assert.True(t.IsRevoked));
        Assert.False(verify.RefreshTokens.Single(t => t.UserId == bystander.Id).IsRevoked);
    }

    [Fact]
    public async Task Ban_PersistsTimestamp_Reason_AndActingAdmin()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var target = SeedUser(db, "target@acme.test");
        var admin = SeedUser(db, "admin@acme.test");
        var sut = NewService(db, TestConfig(), MockUserManager(db));

        var resp = await sut.BanUserAsync(admin.Id, target.Id, "  gian lận thi  ");

        Assert.NotNull(resp.BannedAt);
        Assert.Equal("gian lận thi", resp.BanReason);   // trim

        using var verify = testDb.NewContext();
        var saved = verify.Users.Single(u => u.Id == target.Id);
        Assert.NotNull(saved.BannedAt);
        Assert.Equal(admin.Id, saved.BannedBy);         // ai ra quyết định — phục vụ đối chất
        // Ban KHÔNG được đụng lockout tự động của Identity (hai cơ chế khác nhau).
        Assert.Null(saved.LockoutEnd);
    }

    [Fact]
    public async Task Unban_RestoresLogin_AndClearsBanFields()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var target = SeedUser(db, "target@acme.test", banned: true);
        var sut = NewService(db, TestConfig(), MockUserManager(db));

        var resp = await sut.UnbanUserAsync(target.Id);

        Assert.Null(resp.BannedAt);
        Assert.Null(resp.BanReason);

        // Gỡ ban rồi thì đăng nhập lại được thật (không chỉ là cột null trong DB).
        var login = await sut.LoginAsync(new LoginRequest { Email = target.Email!, Password = "x" });
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
    }

    // ── Bất biến: luôn còn Admin gỡ ban được ─────────────────────────────────────────────────
    [Fact]
    public async Task Ban_LastActiveAdmin_ThrowsConflict_AndDoesNotPersist()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var roleId = SeedAdminRole(db);
        var onlyAdmin = SeedUser(db, "admin@acme.test");
        var other = SeedUser(db, "admin2@acme.test");
        GrantRole(db, onlyAdmin.Id, roleId);
        GrantRole(db, other.Id, roleId);
        // admin2 đã bị cấm từ trước → chỉ còn MỘT admin hoạt động.
        other.BannedAt = DateTime.UtcNow;
        db.SaveChanges();

        var sut = NewService(db, TestConfig(), MockUserManager(db));

        await Assert.ThrowsAsync<AdminActionConflictException>(
            () => sut.BanUserAsync(Guid.NewGuid(), onlyAdmin.Id, "oops"));

        using var verify = testDb.NewContext();
        Assert.Null(verify.Users.Single(u => u.Id == onlyAdmin.Id).BannedAt);
    }

    [Fact]
    public async Task Ban_Admin_WhenAnotherActiveAdminExists_Succeeds()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var roleId = SeedAdminRole(db);
        var a = SeedUser(db, "admin1@acme.test");
        var b = SeedUser(db, "admin2@acme.test");
        GrantRole(db, a.Id, roleId);
        GrantRole(db, b.Id, roleId);

        var sut = NewService(db, TestConfig(), MockUserManager(db));
        var resp = await sut.BanUserAsync(b.Id, a.Id, null);

        Assert.NotNull(resp.BannedAt);
    }

    [Fact]
    public async Task Ban_NonAdminUser_IsNotBlockedByLastAdminGuard()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedAdminRole(db);
        var candidate = SeedUser(db, "candidate@acme.test");   // không có role Admin
        var sut = NewService(db, TestConfig(), MockUserManager(db));

        var resp = await sut.BanUserAsync(Guid.NewGuid(), candidate.Id, null);
        Assert.NotNull(resp.BannedAt);
    }

    [Fact]
    public async Task Ban_UnknownUser_ThrowsKeyNotFound()
    {
        using var testDb = new AuthTestDb();
        var sut = NewService(testDb.Db, TestConfig(), MockUserManager(testDb.Db));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.BanUserAsync(Guid.NewGuid(), Guid.NewGuid(), null));
    }

    [Fact]
    public async Task Unban_UnknownUser_ThrowsKeyNotFound()
    {
        using var testDb = new AuthTestDb();
        var sut = NewService(testDb.Db, TestConfig(), MockUserManager(testDb.Db));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.UnbanUserAsync(Guid.NewGuid()));
    }

    // ── Admin reset mật khẩu hộ ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task AdminResetPassword_RevokesAllRefreshTokens()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var target = SeedUser(db, "victim@acme.test");
        SeedRefreshToken(db, target.Id, "hash-live");
        var userManager = MockUserManager(db);
        var sut = NewService(db, TestConfig(), userManager);

        await sut.AdminResetPasswordAsync(target.Id, "NewPass@123");

        userManager.Verify(m => m.ResetPasswordAsync(It.IsAny<User>(), It.IsAny<string>(), "NewPass@123"), Times.Once);

        using var verify = testDb.NewContext();
        // Đổi mật khẩu mà giữ phiên cũ = không đuổi được kẻ đang chiếm tài khoản.
        Assert.True(verify.RefreshTokens.Single().IsRevoked);
    }

    [Fact]
    public async Task AdminResetPassword_WeakPassword_Throws_AndKeepsSessions()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var target = SeedUser(db, "victim@acme.test");
        SeedRefreshToken(db, target.Id, "hash-live");
        var userManager = MockUserManager(db);
        userManager.Setup(m => m.ResetPasswordAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Password too short" }));
        var sut = NewService(db, TestConfig(), userManager);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.AdminResetPasswordAsync(target.Id, "x"));

        // Reset thất bại → KHÔNG đá người dùng ra khỏi phiên đang chạy.
        using var verify = testDb.NewContext();
        Assert.False(verify.RefreshTokens.Single().IsRevoked);
    }

    [Fact]
    public async Task AdminResetPassword_UnknownUser_ThrowsKeyNotFound()
    {
        using var testDb = new AuthTestDb();
        var sut = NewService(testDb.Db, TestConfig(), MockUserManager(testDb.Db));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.AdminResetPasswordAsync(Guid.NewGuid(), "NewPass@123"));
    }

    // ── Danh sách admin lộ trạng thái ban (FE cần để render nút) ─────────────────────────────
    [Fact]
    public async Task ListAllUsers_SurfacesBanStatus()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedUser(db, "ok@acme.test");
        var banned = SeedUser(db, "banned@acme.test", banned: true);
        banned.BanReason = "gian lận";
        db.SaveChanges();

        var sut = NewService(db, TestConfig(), MockUserManager(db));
        var page = await sut.ListAllUsersAsync(null, null, null, null);

        Assert.Null(page.Items.Single(u => u.Email == "ok@acme.test").BannedAt);
        var row = page.Items.Single(u => u.Email == "banned@acme.test");
        Assert.NotNull(row.BannedAt);
        Assert.Equal("gian lận", row.BanReason);
    }

    // ── Controller: mã lỗi + guard tự-ban ────────────────────────────────────────────────────
    [Fact]
    public async Task Controller_BanSelf_Returns400()
    {
        var adminId = Guid.NewGuid();
        var svc = new Mock<IAuthService>();
        var ctrl = NewController(svc.Object, adminId);

        var result = await ctrl.BanUser(adminId, new BanUserRequest());

        Assert.IsType<BadRequestObjectResult>(result.Result);
        svc.Verify(s => s.BanUserAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Controller_BanUnknownUser_Returns404()
    {
        var svc = new Mock<IAuthService>();
        svc.Setup(s => s.BanUserAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("User not found"));

        var result = await NewController(svc.Object, Guid.NewGuid()).BanUser(Guid.NewGuid(), null);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Controller_BanLastAdmin_Returns409()
    {
        var svc = new Mock<IAuthService>();
        svc.Setup(s => s.BanUserAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AdminActionConflictException("last admin"));

        var result = await NewController(svc.Object, Guid.NewGuid()).BanUser(Guid.NewGuid(), null);
        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Controller_ResetPassword_Returns204_AndWeakPassword400()
    {
        var svc = new Mock<IAuthService>();
        var ctrl = NewController(svc.Object, Guid.NewGuid());

        var ok = await ctrl.ResetUserPassword(Guid.NewGuid(), new AdminResetPasswordRequest { NewPassword = "NewPass@123" });
        Assert.IsType<NoContentResult>(ok);

        svc.Setup(s => s.AdminResetPasswordAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("too weak"));
        var bad = await ctrl.ResetUserPassword(Guid.NewGuid(), new AdminResetPasswordRequest { NewPassword = "x" });
        Assert.IsType<BadRequestObjectResult>(bad);
    }

    // ── A5: endpoint mutation quản trị phải gác platform-role Admin ──────────────────────────
    [Fact]
    public void AdminController_RequiresAdminRole()
    {
        // Gác ở CLASS nên mọi action mới thêm vào đây tự động được bảo vệ. Nếu ai đó nới lỏng
        // (đổi Roles / bỏ attribute) thì test này đỏ trước khi endpoint ban lọt ra ngoài.
        var attr = typeof(AdminController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("Admin", attr!.Roles);
    }

    [Theory]
    [InlineData(nameof(AdminController.BanUser))]
    [InlineData(nameof(AdminController.UnbanUser))]
    [InlineData(nameof(AdminController.ResetUserPassword))]
    public void AdminMutationEndpoints_AreNotAnonymous(string method)
    {
        var m = typeof(AdminController).GetMethod(method)!;
        Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────
    private static User SeedUser(AuthDbContext db, string email, bool banned = false)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            FullName = email,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            BannedAt = banned ? DateTime.UtcNow : null
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static void SeedRefreshToken(AuthDbContext db, Guid userId, string token)
    {
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static Guid SeedAdminRole(AuthDbContext db)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Admin", NormalizedName = "ADMIN" };
        db.Roles.Add(role);
        db.SaveChanges();
        return role.Id;
    }

    private static void GrantRole(AuthDbContext db, Guid userId, Guid roleId)
    {
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static AdminController NewController(IAuthService svc, Guid actingAdminId)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actingAdminId.ToString()), new Claim(ClaimTypes.Role, "Admin")],
            "test");
        return new AdminController(svc)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private static ExternalLoginInfo ExternalInfo(string email, string providerKey)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, "Google User")], "Google");
        return new ClaimsPrincipal(identity) is var principal
            ? new ExternalLoginInfo(principal, "Google", providerKey, "Google")
            : throw new InvalidOperationException();
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

    private static IConfiguration TestConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "isas-test-signing-key-0123456789-abcdef",
            ["Jwt:Issuer"] = "isas-test",
            ["Jwt:Audience"] = "isas-test",
            ["Jwt:AccessTokenMinutes"] = "15",
            ["Jwt:RefreshTokenDays"] = "7",
            ["Internal:Token"] = "test-internal-token"
        }).Build();

    private static Mock<UserManager<User>> MockUserManager(AuthDbContext db)
    {
        var mgr = new Mock<UserManager<User>>(
            Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);

        mgr.Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .Returns<string>(e => Task.FromResult(
                db.Users.FirstOrDefault(u => u.Email!.ToLower() == e.ToLower())));
        mgr.Setup(m => m.CreateAsync(It.IsAny<User>()))
            .Returns<User>(u => { db.Users.Add(u); db.SaveChanges(); return Task.FromResult(IdentityResult.Success); });
        mgr.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>())).ReturnsAsync(IdentityResult.Success);
        mgr.Setup(m => m.AddLoginAsync(It.IsAny<User>(), It.IsAny<UserLoginInfo>())).ReturnsAsync(IdentityResult.Success);
        mgr.Setup(m => m.GetRolesAsync(It.IsAny<User>())).ReturnsAsync(new List<string> { "Candidate" });
        mgr.Setup(m => m.GeneratePasswordResetTokenAsync(It.IsAny<User>())).ReturnsAsync("reset-token");
        mgr.Setup(m => m.ResetPasswordAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);
        return mgr;
    }
}
