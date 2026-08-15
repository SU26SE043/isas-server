using System.Reflection;
using System.Security.Claims;
using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// PlatformAdmin đổi platform-role (AUTH-3) — POST /auth/admin/users/{id}/role.
///
/// ⚠ <see cref="RoleAwareUserManager"/>: mock UserManager ở đây ĐỌC/GHI THẬT vào <c>db.UserRoles</c>
/// thay vì trả một danh sách ghi cứng như <c>MockUserManager</c> của các file khác. Bắt buộc phải
/// vậy: <c>IsLastActiveAdminAsync</c> truy vấn thẳng <c>db.UserRoles</c>, nên một mock trả role rời
/// khỏi DB sẽ khiến guard "Admin cuối cùng" chạy trên dữ liệu khác với thứ ta vừa gán — test xanh mà
/// chẳng chứng minh được gì. Ở đây khẳng định cuối cùng luôn là HÀNG TRONG DB.
/// </summary>
public class AdminPlatformRoleTests
{
    // ── Đường thành công ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ChangeRole_CandidateToEmployer_PersistsNewRole_AndDropsOld()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Candidate");
        SeedRole(db, "Employer");
        var user = SeedUser(db, "u@acme.test");
        GrantRole(db, user.Id, "Candidate");

        var sut = NewService(db);
        var resp = await sut.ChangePlatformRoleAsync(user.Id, "Employer");

        Assert.Equal("Employer", resp.Role);

        // Mô hình 1 role/user: ListAllUsersAsync đọc .FirstOrDefault(), nên cộng dồn role mới mà
        // giữ role cũ sẽ cho ra một hàng hiển thị role tuỳ thứ tự trả về của DB.
        using var verify = testDb.NewContext();
        var roles = RolesOf(verify, user.Id);
        Assert.Equal(["Employer"], roles);
    }

    [Fact]
    public async Task ChangeRole_RevokesRefreshTokens_OfThatUserOnly()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Candidate");
        SeedRole(db, "Employer");
        var target = SeedUser(db, "target@acme.test");
        var bystander = SeedUser(db, "other@acme.test");
        GrantRole(db, target.Id, "Candidate");
        SeedRefreshToken(db, target.Id, "hash-a");
        SeedRefreshToken(db, target.Id, "hash-b");
        SeedRefreshToken(db, bystander.Id, "hash-c");

        await NewService(db).ChangePlatformRoleAsync(target.Id, "Employer");

        using var verify = testDb.NewContext();
        // AUTH-5: không thu hồi thì người dùng cứ gia hạn bằng refresh token cũ và mang quyền CŨ
        // suốt 7 ngày — đổi role sẽ chỉ là cái nhãn trong DB.
        Assert.All(verify.RefreshTokens.Where(t => t.UserId == target.Id), t => Assert.True(t.IsRevoked));
        Assert.False(verify.RefreshTokens.Single(t => t.UserId == bystander.Id).IsRevoked);
    }

    [Fact]
    public async Task ChangeRole_ToSameRole_IsNoOp_AndKeepsSessionsAlive()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Employer");
        var user = SeedUser(db, "u@acme.test");
        GrantRole(db, user.Id, "Employer");
        SeedRefreshToken(db, user.Id, "hash-a");

        var resp = await NewService(db).ChangePlatformRoleAsync(user.Id, "Employer");

        Assert.Equal("Employer", resp.Role);

        // Thao tác không đổi gì mà vẫn đá người dùng ra khỏi phiên = tác dụng phụ thuần tuý, và nó
        // xảy ra đúng lúc admin bấm nhầm rồi chọn lại giá trị cũ.
        using var verify = testDb.NewContext();
        Assert.False(verify.RefreshTokens.Single().IsRevoked);
    }

    // ── Allowlist AUTH-3 ─────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("admin")]        // sai hoa thường — Identity chuẩn hoá NormalizedName nên dễ tưởng là hợp lệ
    [InlineData("")]
    [InlineData("   ")]
    public async Task ChangeRole_OutsideAllowlist_Throws_AndCreatesNoRoleRow(string bad)
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Candidate");
        var user = SeedUser(db, "u@acme.test");
        GrantRole(db, user.Id, "Candidate");

        await Assert.ThrowsAsync<ArgumentException>(
            () => NewService(db).ChangePlatformRoleAsync(user.Id, bad));

        using var verify = testDb.NewContext();
        // Điểm mấu chốt: role được tạo LAZILY, nên một tên gõ sai lọt qua sẽ vừa gán cho user một
        // role không endpoint nào gác, vừa để lại role rác trong bảng.
        Assert.Equal(["Candidate"], RolesOf(verify, user.Id));
        Assert.Equal(["Candidate"], verify.Roles.Select(r => r.Name!).ToArray());
    }

    [Fact]
    public async Task ChangeRole_UnknownUser_ThrowsKeyNotFound()
    {
        using var testDb = new AuthTestDb();
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => NewService(testDb.Db).ChangePlatformRoleAsync(Guid.NewGuid(), "Employer"));
    }

    // ── Bất biến: luôn còn ≥1 Admin hoạt động ────────────────────────────────────────────────
    [Fact]
    public async Task ChangeRole_DemoteLastActiveAdmin_ThrowsConflict_AndKeepsRole()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Admin");
        SeedRole(db, "Candidate");
        var onlyAdmin = SeedUser(db, "admin@acme.test");
        var bannedAdmin = SeedUser(db, "admin2@acme.test");
        GrantRole(db, onlyAdmin.Id, "Admin");
        GrantRole(db, bannedAdmin.Id, "Admin");
        bannedAdmin.BannedAt = DateTime.UtcNow;     // đã bị cấm → chỉ còn MỘT admin hoạt động
        db.SaveChanges();

        await Assert.ThrowsAsync<AdminActionConflictException>(
            () => NewService(db).ChangePlatformRoleAsync(onlyAdmin.Id, "Candidate"));

        // Hạ nốt Admin cuối cùng thì không còn ai nâng lại được cho ai — chỉ sửa được bằng tay trong DB.
        using var verify = testDb.NewContext();
        Assert.Equal(["Admin"], RolesOf(verify, onlyAdmin.Id));
    }

    [Fact]
    public async Task ChangeRole_DemoteAdmin_WhenAnotherActiveAdminExists_Succeeds()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Admin");
        SeedRole(db, "Candidate");
        var a = SeedUser(db, "admin1@acme.test");
        var b = SeedUser(db, "admin2@acme.test");
        GrantRole(db, a.Id, "Admin");
        GrantRole(db, b.Id, "Admin");

        var resp = await NewService(db).ChangePlatformRoleAsync(a.Id, "Candidate");

        Assert.Equal("Candidate", resp.Role);
    }

    [Fact]
    public async Task ChangeRole_PromoteToAdmin_IsNotBlockedByLastAdminGuard()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Admin");
        SeedRole(db, "Candidate");
        var onlyAdmin = SeedUser(db, "admin@acme.test");
        GrantRole(db, onlyAdmin.Id, "Admin");
        var candidate = SeedUser(db, "c@acme.test");
        GrantRole(db, candidate.Id, "Candidate");

        // Guard chỉ chặn chiều HẠ CẤP. Chặn cả chiều nâng thì hệ thống một-admin không bao giờ có
        // admin thứ hai — tức là tự khoá đúng đường thoát khỏi tình trạng một-admin.
        var resp = await NewService(db).ChangePlatformRoleAsync(candidate.Id, "Admin");
        Assert.Equal("Admin", resp.Role);
    }

    // ── Bất biến org: "thành viên org ⇒ platform-role Employer" ──────────────────────────────
    [Fact]
    public async Task ChangeRole_LeavingEmployer_WhileStillOrgMember_ThrowsConflict()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Employer");
        SeedRole(db, "Candidate");
        var user = SeedUser(db, "hr@acme.test");
        GrantRole(db, user.Id, "Employer");
        SeedMembership(db, user.Id, OrgRole.OrgAdmin);

        await Assert.ThrowsAsync<AdminActionConflictException>(
            () => NewService(db).ChangePlatformRoleAsync(user.Id, "Candidate"));

        // Không chặn thì đây là đường VÒNG QUA guard "cấm hạ OrgAdmin cuối cùng" của A6b
        // (ChangeOrgMemberRoleAsync): org mất sạch người lo billing/thành viên, không cảnh báo nào.
        using var verify = testDb.NewContext();
        Assert.Equal(["Employer"], RolesOf(verify, user.Id));
        Assert.Single(verify.OrgMembers.Where(m => m.UserId == user.Id));
    }

    [Fact]
    public async Task ChangeRole_OrgMember_PromotedToAdmin_IsAlsoBlocked()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Employer");
        SeedRole(db, "Admin");
        var user = SeedUser(db, "hr@acme.test");
        GrantRole(db, user.Id, "Employer");
        SeedMembership(db, user.Id, OrgRole.HrMember);

        // Admin cũng không phải Employer → cùng trạng thái hỏng: JWT mang org_id + org_role trong
        // khi platform-role không cho qua endpoint Employer nào.
        await Assert.ThrowsAsync<AdminActionConflictException>(
            () => NewService(db).ChangePlatformRoleAsync(user.Id, "Admin"));
    }

    [Fact]
    public async Task ChangeRole_ToEmployer_WhileOrgMember_IsAllowed()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Employer");
        SeedRole(db, "Admin");
        var user = SeedUser(db, "boss@acme.test");
        GrantRole(db, user.Id, "Admin");
        SeedMembership(db, user.Id, OrgRole.OrgAdmin);
        // Phải có admin thứ hai, nếu không guard "Admin cuối cùng" bắn trước và test đo nhầm thứ.
        GrantRole(db, SeedUser(db, "admin2@acme.test").Id, "Admin");

        // Guard chỉ nhắm chiều RỜI KHỎI Employer. Chặn cả chiều vào thì không sửa nổi đúng cái
        // trạng thái lệch mà nó sinh ra để bảo vệ.
        var resp = await NewService(db).ChangePlatformRoleAsync(user.Id, "Employer");
        Assert.Equal("Employer", resp.Role);
    }

    [Fact]
    public async Task ChangeRole_ToCandidate_WhenNotOrgMember_Succeeds()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedRole(db, "Employer");
        SeedRole(db, "Candidate");
        var user = SeedUser(db, "solo@acme.test");
        GrantRole(db, user.Id, "Employer");

        // Guard org không được bắt oan người không thuộc org nào — nếu bắt, tính năng gần như vô dụng.
        var resp = await NewService(db).ChangePlatformRoleAsync(user.Id, "Candidate");
        Assert.Equal("Candidate", resp.Role);
    }

    // ── Controller: mã lỗi + guard tự đổi mình ───────────────────────────────────────────────
    [Fact]
    public async Task Controller_ChangeOwnRole_Returns400_AndNeverCallsService()
    {
        var adminId = Guid.NewGuid();
        var svc = new Mock<IAuthService>();

        var result = await NewController(svc.Object, adminId)
            .ChangeUserRole(adminId, new ChangePlatformRoleRequest { Role = "Candidate" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        svc.Verify(s => s.ChangePlatformRoleAsync(It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(typeof(KeyNotFoundException), typeof(NotFoundObjectResult))]
    [InlineData(typeof(AdminActionConflictException), typeof(ConflictObjectResult))]
    [InlineData(typeof(ArgumentException), typeof(BadRequestObjectResult))]
    public async Task Controller_MapsServiceErrorsToStatusCodes(Type thrown, Type expected)
    {
        var svc = new Mock<IAuthService>();
        svc.Setup(s => s.ChangePlatformRoleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(thrown, "boom")!);

        var result = await NewController(svc.Object, Guid.NewGuid())
            .ChangeUserRole(Guid.NewGuid(), new ChangePlatformRoleRequest { Role = "Employer" });

        Assert.IsType(expected, result.Result);
    }

    [Fact]
    public void ChangeUserRole_IsNotAnonymous()
    {
        // Gác Roles="Admin" ở cấp class (AdminController) — test này chặn việc ai đó nới lỏng riêng
        // action leo thang đặc quyền này bằng [AllowAnonymous].
        var m = typeof(AdminController).GetMethod(nameof(AdminController.ChangeUserRole))!;
        Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────
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

    private static void SeedRole(AuthDbContext db, string name)
    {
        db.Roles.Add(new Role { Id = Guid.NewGuid(), Name = name, NormalizedName = name.ToUpperInvariant() });
        db.SaveChanges();
    }

    private static void GrantRole(AuthDbContext db, Guid userId, string roleName)
    {
        var roleId = db.Roles.Single(r => r.Name == roleName).Id;
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static string[] RolesOf(AuthDbContext db, Guid userId) =>
        db.UserRoles.Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
            .OrderBy(n => n)
            .ToArray();

    private static void SeedMembership(AuthDbContext db, Guid userId, OrgRole orgRole)
    {
        var org = new Organization { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTime.UtcNow };
        db.Organizations.Add(org);
        db.OrgMembers.Add(new OrgMember
        {
            OrgId = org.Id,
            UserId = userId,
            OrgRole = orgRole,
            JoinedAt = DateTime.UtcNow
        });
        db.SaveChanges();
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

    private static Isas.AuthService.Services.AuthService NewService(AuthDbContext db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "isas-test-signing-key-0123456789-abcdef",
            ["Jwt:Issuer"] = "isas-test",
            ["Jwt:Audience"] = "isas-test",
            ["Jwt:AccessTokenMinutes"] = "15",
            ["Jwt:RefreshTokenDays"] = "7"
        }).Build();

        var userManager = RoleAwareUserManager(db);
        var roleManager = new Mock<RoleManager<Role>>(Mock.Of<IRoleStore<Role>>(), null!, null!, null!, null!);
        roleManager.Setup(m => m.RoleExistsAsync(It.IsAny<string>()))
            .Returns<string>(n => Task.FromResult(db.Roles.Any(r => r.Name == n)));
        roleManager.Setup(m => m.CreateAsync(It.IsAny<Role>()))
            .Returns<Role>(r => { db.Roles.Add(r); db.SaveChanges(); return Task.FromResult(IdentityResult.Success); });
        var signInManager = new Mock<SignInManager<User>>(userManager.Object, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);

        return new Isas.AuthService.Services.AuthService(
            db, new JwtService(config), userManager.Object, roleManager.Object, config, signInManager.Object);
    }

    /// <summary>
    /// UserManager giả nhưng role đi THẲNG vào <c>db.UserRoles</c> — xem chú thích đầu class về lý do.
    /// </summary>
    private static Mock<UserManager<User>> RoleAwareUserManager(AuthDbContext db)
    {
        var mgr = new Mock<UserManager<User>>(
            Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);

        mgr.Setup(m => m.GetRolesAsync(It.IsAny<User>()))
            .Returns<User>(u => Task.FromResult<IList<string>>(RolesOf(db, u.Id).ToList()));

        mgr.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>()))
            .Returns<User, string>((u, r) =>
            {
                GrantRole(db, u.Id, r);
                return Task.FromResult(IdentityResult.Success);
            });

        mgr.Setup(m => m.RemoveFromRolesAsync(It.IsAny<User>(), It.IsAny<IEnumerable<string>>()))
            .Returns<User, IEnumerable<string>>((u, names) =>
            {
                var ids = db.Roles.Where(r => names.Contains(r.Name!)).Select(r => r.Id).ToList();
                db.UserRoles.RemoveRange(db.UserRoles.Where(ur => ur.UserId == u.Id && ids.Contains(ur.RoleId)));
                db.SaveChanges();
                return Task.FromResult(IdentityResult.Success);
            });

        return mgr;
    }
}
