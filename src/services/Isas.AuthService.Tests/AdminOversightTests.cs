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
        var orgs = await svc.ListAllOrganizationsAsync(null, null, null);

        Assert.Equal(2, orgs.Items.Count);
        Assert.Null(orgs.NextCursor);   // < default limit → last page
        Assert.Equal(2, orgs.Items.Single(o => o.Name == "Acme").MemberCount);
        Assert.Equal(1, orgs.Items.Single(o => o.Name == "Globex").MemberCount);
    }

    [Fact]
    public async Task ListAllOrganizations_SearchByName_FiltersCaseInsensitive()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedOrg(db, "Acme Corp");
        SeedOrg(db, "Globex");

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var orgs = await svc.ListAllOrganizationsAsync("acme", null, null);

        Assert.Single(orgs.Items);
        Assert.Equal("Acme Corp", orgs.Items[0].Name);
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
        var users = await svc.ListAllUsersAsync(null, null, null, null);

        Assert.Equal(3, users.Items.Count);   // cross-org: cả 3 user

        var acme = users.Items.Single(u => u.Email == "hr@acme.test");
        Assert.Equal(orgA, acme.OrgId);
        Assert.Equal("Acme", acme.OrgName);
        Assert.Equal("HrMember", acme.OrgRole);
        Assert.Equal("Employer", acme.Role);   // platform-role từ MockUserManager

        var globex = users.Items.Single(u => u.Email == "boss@globex.test");
        Assert.Equal(orgB, globex.OrgId);
        Assert.Equal("OrgAdmin", globex.OrgRole);

        var solo = users.Items.Single(u => u.Email == "solo@candidate.test");
        Assert.Null(solo.OrgId);
        Assert.Null(solo.OrgName);
        Assert.Null(solo.OrgRole);
    }

    [Fact]
    public async Task R12_GetUser_ThemThongTinOrgNeuLaMember()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme");
        var user = SeedUser(db, "member@acme.test");
        db.OrgMembers.Add(new OrgMember { OrgId = orgId, UserId = user.Id, OrgRole = OrgRole.OrgAdmin });
        db.SaveChanges();

        var response = await NewService(db, TestConfig(), MockUserManager(db)).GetUserAsync(user.Id);

        Assert.Equal(orgId, response.OrgId);
        Assert.Equal("Acme", response.OrgName);
        Assert.Equal("OrgAdmin", response.OrgRole);
    }

    [Fact]
    public async Task R12_GetUser_KhongThuocOrg_TraNull()
    {
        using var testDb = new AuthTestDb();
        var user = SeedUser(testDb.Db, "solo@candidate.test");

        var response = await NewService(testDb.Db, TestConfig(), MockUserManager(testDb.Db)).GetUserAsync(user.Id);

        Assert.Null(response.OrgId);
        Assert.Null(response.OrgName);
        Assert.Null(response.OrgRole);
    }

    [Fact]
    public async Task ListAllUsers_SearchByEmail_Filters()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedUser(db, "match@acme.test");
        SeedUser(db, "other@globex.test");

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var users = await svc.ListAllUsersAsync(null, "acme", null, null);

        Assert.Single(users.Items);
        Assert.Equal("match@acme.test", users.Items[0].Email);
    }

    [Fact]
    public async Task ListAllOrganizations_Keyset_PagesWithoutOverlapOrGap()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
            SeedOrgAt(db, $"Org{i}", t0.AddMinutes(i));

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await svc.ListAllOrganizationsAsync(null, cursor, 2);
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(o => o.Name));
            cursor = page.NextCursor;
            Assert.True(++pages <= 10, "paging did not terminate");
        } while (cursor is not null);

        Assert.Equal(new[] { "Org4", "Org3", "Org2", "Org1", "Org0" }, seen.ToArray());
    }

    [Fact]
    public async Task ListAllOrganizations_Keyset_TiebreakerOnIdenticalCreatedAt()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var same = new DateTime(2026, 7, 2, 9, 0, 0, DateTimeKind.Utc);
        SeedOrgAt(db, "T1", same);
        SeedOrgAt(db, "T2", same);
        SeedOrgAt(db, "T3", same);

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var seen = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 5 && (i == 0 || cursor is not null); i++)
        {
            var page = await svc.ListAllOrganizationsAsync(null, cursor, 1);
            seen.AddRange(page.Items.Select(o => o.Name));
            cursor = page.NextCursor;
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task R11_ListAllOrganizations_MalformedCursor_Throws()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedOrg(db, "One");
        SeedOrg(db, "Two");

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ListAllOrganizationsAsync(null, "###bad###", null));
    }

    [Fact]
    public async Task R11_ListAllUsers_LimitKhongDuong_Throws()
    {
        using var testDb = new AuthTestDb();
        var svc = NewService(testDb.Db, TestConfig(), MockUserManager(testDb.Db));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.ListAllUsersAsync(null, null, null, 0));
    }

    [Fact]
    public async Task ListAllUsers_RoleFilter_PushedDownToQuery()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var employer = new Role { Id = Guid.NewGuid(), Name = "Employer", NormalizedName = "EMPLOYER" };
        var candidate = new Role { Id = Guid.NewGuid(), Name = "Candidate", NormalizedName = "CANDIDATE" };
        db.Roles.AddRange(employer, candidate);
        db.SaveChanges();

        var e1 = SeedUser(db, "e1@x.test");
        var e2 = SeedUser(db, "e2@x.test");
        var c1 = SeedUser(db, "c1@x.test");
        db.UserRoles.AddRange(
            new UserRole { UserId = e1.Id, RoleId = employer.Id },
            new UserRole { UserId = e2.Id, RoleId = employer.Id },
            new UserRole { UserId = c1.Id, RoleId = candidate.Id });
        db.SaveChanges();

        // RoleManager resolves "Employer" → its Role (Identity normalization) so the query joins UserRoles.
        var svc = NewService(db, TestConfig(), MockUserManager(db), MockRoleManagerFinding(employer));
        var page = await svc.ListAllUsersAsync("Employer", null, null, null);

        Assert.Equal(2, page.Items.Count);   // only the two Employer-role users, NOT the candidate
        Assert.All(page.Items, u => Assert.StartsWith("e", u.Email));
    }

    [Fact]
    public async Task ListAllUsers_UnknownRole_ReturnsEmpty()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        SeedUser(db, "someone@x.test");

        // Default RoleManager mock: FindByNameAsync returns null → unknown role → empty page.
        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var page = await svc.ListAllUsersAsync("Ghost", null, null, null);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    // ── helpers (mirror OrgMemberServiceTests) ──────────────────────────────
    private static Guid SeedOrg(AuthDbContext db, string name)
        => SeedOrgAt(db, name, DateTime.UtcNow);

    private static Guid SeedOrgAt(AuthDbContext db, string name, DateTime createdAt)
    {
        var org = new Organization { Id = Guid.NewGuid(), Name = name, CreatedAt = createdAt };
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
        => NewService(db, config, userManager, MockRoleManager());

    private static Isas.AuthService.Services.AuthService NewService(
        AuthDbContext db, IConfiguration config, Mock<UserManager<User>> userManager, Mock<RoleManager<Role>> roleManager)
    {
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
        return mgr;   // FindByNameAsync not set up → returns null (unknown role)
    }

    private static Mock<RoleManager<Role>> MockRoleManagerFinding(Role role)
    {
        var mgr = new Mock<RoleManager<Role>>(Mock.Of<IRoleStore<Role>>(), null!, null!, null!, null!);
        mgr.Setup(m => m.FindByNameAsync(role.Name!)).ReturnsAsync(role);
        return mgr;
    }

    private static Mock<SignInManager<User>> MockSignInManager(UserManager<User> userManager) =>
        new(userManager, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);
}
