using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// A6b (AUTH-4/AUTH-8) — OrgAdmin quản thành viên org: đổi role · xoá · joined_at thật.
/// AuthService thật + AuthDbContext SQLite (seed OrgMember trực tiếp). Idiom theo OrgMemberServiceTests.
/// </summary>
public class OrgMemberManagementTests
{
    // ── ChangeOrgMemberRoleAsync ────────────────────────────────────────────

    [Fact]
    public async Task ChangeRole_PromoteHrMemberToOrgAdmin_Persists()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        SeedMember(db, orgId, SeedUser(db, "admin@acme.test").Id, OrgRole.OrgAdmin);
        var hr = SeedUser(db, "hr@acme.test");
        SeedMember(db, orgId, hr.Id, OrgRole.HrMember);
        var svc = NewService(db);

        var resp = await svc.ChangeOrgMemberRoleAsync(orgId, hr.Id, OrgRole.OrgAdmin);

        Assert.Equal("OrgAdmin", resp.OrgRole);
        using var verify = testDb.NewContext();
        Assert.Equal(OrgRole.OrgAdmin, verify.OrgMembers.Single(m => m.UserId == hr.Id).OrgRole);
    }

    [Fact]
    public async Task ChangeRole_DemoteOrgAdmin_WhenAnotherAdminExists_Persists()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        var admin1 = SeedUser(db, "admin1@acme.test");
        var admin2 = SeedUser(db, "admin2@acme.test");
        SeedMember(db, orgId, admin1.Id, OrgRole.OrgAdmin);
        SeedMember(db, orgId, admin2.Id, OrgRole.OrgAdmin);
        var svc = NewService(db);

        var resp = await svc.ChangeOrgMemberRoleAsync(orgId, admin2.Id, OrgRole.HrMember);

        Assert.Equal("HrMember", resp.OrgRole);
        using var verify = testDb.NewContext();
        Assert.Equal(OrgRole.HrMember, verify.OrgMembers.Single(m => m.UserId == admin2.Id).OrgRole);
    }

    [Fact]
    public async Task ChangeRole_DemoteLastOrgAdmin_ThrowsConflict_NoChange()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        var admin = SeedUser(db, "admin@acme.test");
        SeedMember(db, orgId, admin.Id, OrgRole.OrgAdmin);
        SeedMember(db, orgId, SeedUser(db, "hr@acme.test").Id, OrgRole.HrMember);
        var svc = NewService(db);

        await Assert.ThrowsAsync<OrgMemberConflictException>(
            () => svc.ChangeOrgMemberRoleAsync(orgId, admin.Id, OrgRole.HrMember));

        using var verify = testDb.NewContext();
        Assert.Equal(OrgRole.OrgAdmin, verify.OrgMembers.Single(m => m.UserId == admin.Id).OrgRole);
    }

    [Fact]
    public async Task ChangeRole_MemberNotInOrg_ThrowsNotFound()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        var svc = NewService(db);

        await Assert.ThrowsAsync<OrgMemberNotFoundException>(
            () => svc.ChangeOrgMemberRoleAsync(orgId, Guid.NewGuid(), OrgRole.HrMember));
    }

    [Fact]
    public async Task ChangeRole_TargetInDifferentOrg_ThrowsNotFound()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgA = SeedOrg(db, "Acme");
        var orgB = SeedOrg(db, "Globex");
        var other = SeedUser(db, "boss@globex.test");
        SeedMember(db, orgB, other.Id, OrgRole.HrMember);
        var svc = NewService(db);

        // caller (orgA) không đụng được member của orgB
        await Assert.ThrowsAsync<OrgMemberNotFoundException>(
            () => svc.ChangeOrgMemberRoleAsync(orgA, other.Id, OrgRole.OrgAdmin));
    }

    // ── RemoveOrgMemberAsync ────────────────────────────────────────────────

    [Fact]
    public async Task Remove_HrMember_DropsMembershipRow_KeepsUserAccount()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        SeedMember(db, orgId, SeedUser(db, "admin@acme.test").Id, OrgRole.OrgAdmin);
        var hr = SeedUser(db, "hr@acme.test");
        SeedMember(db, orgId, hr.Id, OrgRole.HrMember);
        var svc = NewService(db);

        await svc.RemoveOrgMemberAsync(orgId, hr.Id);

        using var verify = testDb.NewContext();
        Assert.False(verify.OrgMembers.Any(m => m.UserId == hr.Id));   // membership xoá
        Assert.True(verify.Users.Any(u => u.Id == hr.Id));             // account giữ nguyên
    }

    [Fact]
    public async Task Remove_OrgAdmin_WhenAnotherAdminExists_DropsRow()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        var admin1 = SeedUser(db, "admin1@acme.test");
        var admin2 = SeedUser(db, "admin2@acme.test");
        SeedMember(db, orgId, admin1.Id, OrgRole.OrgAdmin);
        SeedMember(db, orgId, admin2.Id, OrgRole.OrgAdmin);
        var svc = NewService(db);

        await svc.RemoveOrgMemberAsync(orgId, admin2.Id);

        using var verify = testDb.NewContext();
        Assert.False(verify.OrgMembers.Any(m => m.UserId == admin2.Id));
        Assert.True(verify.OrgMembers.Any(m => m.UserId == admin1.Id));
    }

    [Fact]
    public async Task Remove_LastOrgAdmin_ThrowsConflict_NoChange()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        var admin = SeedUser(db, "admin@acme.test");
        SeedMember(db, orgId, admin.Id, OrgRole.OrgAdmin);
        SeedMember(db, orgId, SeedUser(db, "hr@acme.test").Id, OrgRole.HrMember);
        var svc = NewService(db);

        await Assert.ThrowsAsync<OrgMemberConflictException>(
            () => svc.RemoveOrgMemberAsync(orgId, admin.Id));

        using var verify = testDb.NewContext();
        Assert.True(verify.OrgMembers.Any(m => m.UserId == admin.Id));
    }

    [Fact]
    public async Task Remove_MemberNotInOrg_ThrowsNotFound()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        var svc = NewService(db);

        await Assert.ThrowsAsync<OrgMemberNotFoundException>(
            () => svc.RemoveOrgMemberAsync(orgId, Guid.NewGuid()));
    }

    // ── JoinedAt thật (không còn proxy User.CreatedAt) ──────────────────────

    [Fact]
    public async Task AddOrgMember_SetsRealJoinedAt_NearNow()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        var svc = NewService(db);

        var before = DateTime.UtcNow.AddSeconds(-2);
        var resp = await svc.AddOrgMemberAsync(orgId, "hr@acme.test", "HR");
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.NotEqual(default, resp.JoinedAt);
        Assert.InRange(resp.JoinedAt, before, after);

        using var verify = testDb.NewContext();
        Assert.Equal(resp.JoinedAt, verify.OrgMembers.Single().JoinedAt);
    }

    [Fact]
    public async Task ListOrgMembers_ReturnsColumnJoinedAt_NotUserCreatedAt()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        // account tạo lâu rồi, nhưng gia nhập org gần đây → list phải trả joined_at, KHÔNG phải CreatedAt
        var user = SeedUser(db, "hr@acme.test", createdAt: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var joinedAt = new DateTime(2025, 6, 15, 8, 30, 0, DateTimeKind.Utc);
        SeedMember(db, orgId, user.Id, OrgRole.HrMember, joinedAt);
        var svc = NewService(db);

        var members = await svc.ListOrgMembersAsync(orgId);

        var m = Assert.Single(members);
        Assert.Equal(joinedAt, m.JoinedAt);
        Assert.NotEqual(user.CreatedAt, m.JoinedAt);
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static Guid SeedOrg(AuthDbContext db, string name)
    {
        var org = new Organization { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow };
        db.Organizations.Add(org);
        db.SaveChanges();
        return org.Id;
    }

    private static User SeedUser(AuthDbContext db, string email, DateTime? createdAt = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            FullName = email,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static void SeedMember(AuthDbContext db, Guid orgId, Guid userId, OrgRole role, DateTime? joinedAt = null)
    {
        db.OrgMembers.Add(new OrgMember
        {
            OrgId = orgId,
            UserId = userId,
            OrgRole = role,
            JoinedAt = joinedAt ?? DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static Isas.AuthService.Services.AuthService NewService(AuthDbContext db)
    {
        var config = TestConfig();
        var userManager = MockUserManager(db);
        var roleManager = MockRoleManager();
        var signInManager = MockSignInManager(userManager.Object);
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
        mgr.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);
        mgr.Setup(m => m.GetRolesAsync(It.IsAny<User>()))
            .ReturnsAsync(new List<string> { "Employer" });
        return mgr;
    }

    private static Mock<RoleManager<Role>> MockRoleManager()
    {
        var mgr = new Mock<RoleManager<Role>>(Mock.Of<IRoleStore<Role>>(), null!, null!, null!, null!);
        mgr.Setup(m => m.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        return mgr;
    }

    private static Mock<SignInManager<User>> MockSignInManager(UserManager<User> userManager) =>
        new(userManager, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);
}
