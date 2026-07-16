using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// AUTH-7 — PlatformAdmin oversight (read-only, cross-org). AuthService thật + AuthDbContext SQLite;
/// UserManager mock (GetRolesAsync). Verify ListAll trả dữ liệu XUYÊN org (khác ListOrgMembers chỉ 1 org).
/// Idiom helpers theo OrgMemberServiceTests (SeedOrg/SeedUser/NewService/MockUserManager).
/// </summary>
public class AdminOversightTests
{
    [Fact]
    public async Task ListAllOrganizations_ReturnsAllOrgs_CrossOrg_WithMemberCount()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgA = SeedOrg(db, "Acme");
        var orgB = SeedOrg(db, "Globex");
        var a1 = SeedUser(db, "a1@acme.test");
        var a2 = SeedUser(db, "a2@acme.test");
        var b1 = SeedUser(db, "b1@globex.test");
        db.OrgMembers.AddRange(
            new OrgMember { OrgId = orgA, UserId = a1.Id, OrgRole = OrgRole.OrgAdmin },
            new OrgMember { OrgId = orgA, UserId = a2.Id, OrgRole = OrgRole.HrMember },
            new OrgMember { OrgId = orgB, UserId = b1.Id, OrgRole = OrgRole.OrgAdmin });
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var orgs = await svc.ListAllOrganizationsAsync(null);

        Assert.Equal(2, orgs.Count);
        Assert.Equal(2, orgs.Single(o => o.Name == "Acme").MemberCount);
        Assert.Equal(1, orgs.Single(o => o.Name == "Globex").MemberCount);
    }

    [Fact]
    public async Task ListAllOrganizations_SearchByName_FiltersCaseInsensitive()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedOrg(db, "Acme Corp");
        SeedOrg(db, "Globex");

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var orgs = await svc.ListAllOrganizationsAsync("acme");

        Assert.Single(orgs);
        Assert.Equal("Acme Corp", orgs[0].Name);
    }

    [Fact]
    public async Task ListAllUsers_ReturnsUsersAcrossOrgs_WithOrgMembershipAndRole()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgA = SeedOrg(db, "Acme");
        var orgB = SeedOrg(db, "Globex");
        var inA = SeedUser(db, "hr@acme.test");
        var inB = SeedUser(db, "boss@globex.test");
        var candidate = SeedUser(db, "solo@candidate.test");   // không thuộc org nào
        db.OrgMembers.AddRange(
            new OrgMember { OrgId = orgA, UserId = inA.Id, OrgRole = OrgRole.HrMember },
            new OrgMember { OrgId = orgB, UserId = inB.Id, OrgRole = OrgRole.OrgAdmin });
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var users = await svc.ListAllUsersAsync(null, null);

        Assert.Equal(3, users.Count);   // cross-org: cả 3 user

        var acme = users.Single(u => u.Email == "hr@acme.test");
        Assert.Equal(orgA, acme.OrgId);
        Assert.Equal("Acme", acme.OrgName);
        Assert.Equal("HrMember", acme.OrgRole);
        Assert.Equal("Employer", acme.Role);   // platform-role từ MockUserManager

        var globex = users.Single(u => u.Email == "boss@globex.test");
        Assert.Equal(orgB, globex.OrgId);
        Assert.Equal("OrgAdmin", globex.OrgRole);

        var solo = users.Single(u => u.Email == "solo@candidate.test");
        Assert.Null(solo.OrgId);
        Assert.Null(solo.OrgName);
        Assert.Null(solo.OrgRole);
    }

    [Fact]
    public async Task ListAllUsers_SearchByEmail_Filters()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedUser(db, "match@acme.test");
        SeedUser(db, "other@globex.test");

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var users = await svc.ListAllUsersAsync(null, "acme");

        Assert.Single(users);
        Assert.Equal("match@acme.test", users[0].Email);
    }

    // ── helpers (mirror OrgMemberServiceTests) ──────────────────────────────
    private static Guid SeedOrg(AuthDbContext db, string name)
    {
        var org = new Organization { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow };
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
