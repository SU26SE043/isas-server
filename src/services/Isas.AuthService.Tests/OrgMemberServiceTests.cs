using System.IdentityModel.Tokens.Jwt;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// A6 (AUTH-4/AUTH-8) — OrgAdmin mời/tạo HrMember vào org. AuthService thật + AuthDbContext SQLite;
/// UserManager mock (CreateAsync passwordless persist vào SQLite, FindByEmailAsync đọc lại) → verify
/// membership persist đúng org + role + dedup 409. Idiom theo ProvisionCandidateTests/RegisterOrgTests.
/// </summary>
public class OrgMemberServiceTests
{
    [Fact]
    public async Task OrgAdmin_AddHrMember_CreatesHrMemberRow_InCallerOrg_AndEmployerRole()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();
        var orgId = SeedOrg(db, "Acme Corp");
        var userManager = MockUserManager(db);
        var svc = NewService(db, config, userManager);

        var resp = await svc.AddOrgMemberAsync(orgId, "hr1@acme.test", "HR One");

        // response phản ánh member vừa tạo
        Assert.Equal("hr1@acme.test", resp.Email);
        Assert.Equal("HrMember", resp.OrgRole);
        Assert.NotEqual(Guid.Empty, resp.UserId);

        // OrgMember row đúng org + role HrMember (đọc bằng context mới → đã persist)
        using var verify = testDb.NewContext();
        var member = verify.OrgMembers.Single();
        Assert.Equal(orgId, member.OrgId);
        Assert.Equal(resp.UserId, member.UserId);
        Assert.Equal(OrgRole.HrMember, member.OrgRole);

        // user tạo với role Employer (AUTH: HR = platform-role Employer)
        userManager.Verify(m => m.AddToRoleAsync(It.IsAny<User>(), "Employer"), Times.Once);
    }

    [Fact]
    public async Task AddHrMember_UsesPasswordlessCreate_NotPasswordOverload()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme Corp");
        var userManager = MockUserManager(db);
        var svc = NewService(db, TestConfig(), userManager);

        await svc.AddOrgMemberAsync(orgId, "hr2@acme.test", "HR Two");

        // passwordless (mẫu ProvisionCandidate) — CreateAsync(user) không password
        userManager.Verify(m => m.CreateAsync(It.IsAny<User>()), Times.Once);
        userManager.Verify(m => m.CreateAsync(It.IsAny<User>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DuplicateEmail_AlreadyMemberOfOrg_ThrowsConflict_NoNewUser()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme Corp");
        var existing = SeedUser(db, "dup@acme.test");
        db.OrgMembers.Add(new OrgMember { OrgId = orgId, UserId = existing.Id, OrgRole = OrgRole.HrMember });
        db.SaveChanges();

        var userManager = MockUserManager(db);
        var svc = NewService(db, TestConfig(), userManager);

        await Assert.ThrowsAsync<OrgMemberConflictException>(
            () => svc.AddOrgMemberAsync(orgId, "dup@acme.test", "Dup"));

        using var verify = testDb.NewContext();
        Assert.Single(verify.Users);          // không tạo account thứ 2
        Assert.Single(verify.OrgMembers);     // không thêm membership
        userManager.Verify(m => m.CreateAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task ExistingEmail_RegisteredElsewhere_ThrowsConflict()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme Corp");
        SeedUser(db, "taken@somewhere.test");   // user tồn tại nhưng KHÔNG thuộc org này
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));

        // email UNIQUE → không thể tạo account trùng → 409 (ngoài scope: đổi-role account có sẵn)
        await Assert.ThrowsAsync<OrgMemberConflictException>(
            () => svc.AddOrgMemberAsync(orgId, "taken@somewhere.test", "X"));
    }

    [Fact]
    public async Task ListOrgMembers_ReturnsOnlyCallerOrgMembers()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgA = SeedOrg(db, "Acme");
        var orgB = SeedOrg(db, "Globex");
        var admin = SeedUser(db, "admin@acme.test");
        var hr = SeedUser(db, "hr@acme.test");
        var other = SeedUser(db, "boss@globex.test");
        db.OrgMembers.AddRange(
            new OrgMember { OrgId = orgA, UserId = admin.Id, OrgRole = OrgRole.OrgAdmin },
            new OrgMember { OrgId = orgA, UserId = hr.Id, OrgRole = OrgRole.HrMember },
            new OrgMember { OrgId = orgB, UserId = other.Id, OrgRole = OrgRole.OrgAdmin });
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));

        var members = await svc.ListOrgMembersAsync(orgA);

        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.Email == "admin@acme.test" && m.OrgRole == "OrgAdmin");
        Assert.Contains(members, m => m.Email == "hr@acme.test" && m.OrgRole == "HrMember");
        Assert.DoesNotContain(members, m => m.Email == "boss@globex.test");   // org khác không lọt
    }

    [Fact]
    public async Task AddedHrMember_MembershipYieldsHrMemberTokenClaim()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();
        var orgId = SeedOrg(db, "Acme Corp");
        var svc = NewService(db, config, MockUserManager(db));

        var resp = await svc.AddOrgMemberAsync(orgId, "hr@acme.test", "HR");

        // A2 loop: membership persist → JWT khi HR login mang org_id + org_role=HrMember
        using var verify = testDb.NewContext();
        var user = verify.Users.Single(u => u.Id == resp.UserId);
        var membership = verify.OrgMembers.Single(m => m.UserId == resp.UserId);

        var token = new JwtService(config).GenerateAccessToken(user, new[] { "Employer" }, membership);
        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(orgId.ToString(), decoded.Claims.Single(c => c.Type == "org_id").Value);
        Assert.Equal("HrMember", decoded.Claims.Single(c => c.Type == "org_role").Value);
    }

    [Fact]
    public async Task GetOrganization_ReturnsNameTaxCode_AndMemberCount()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var org = new Organization { Id = Guid.NewGuid(), Name = "Acme", TaxCode = "0123456789", CreatedAt = DateTime.UtcNow };
        db.Organizations.Add(org);
        var a = SeedUser(db, "a@acme.test");
        var b = SeedUser(db, "b@acme.test");
        db.OrgMembers.AddRange(
            new OrgMember { OrgId = org.Id, UserId = a.Id, OrgRole = OrgRole.OrgAdmin },
            new OrgMember { OrgId = org.Id, UserId = b.Id, OrgRole = OrgRole.HrMember });
        db.SaveChanges();

        var svc = NewService(db, TestConfig(), MockUserManager(db));
        var resp = await svc.GetOrganizationAsync(org.Id);

        Assert.Equal("Acme", resp.Name);
        Assert.Equal("0123456789", resp.TaxCode);
        Assert.Equal(2, resp.MemberCount);
    }

    [Fact]
    public async Task GetOrganization_Missing_ThrowsKeyNotFound()
    {
        using var testDb = new AuthTestDb();
        var svc = NewService(testDb.Db, TestConfig(), MockUserManager(testDb.Db));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetOrganizationAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task UpdateOrganization_ChangesNameAndTaxCode_Persists()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Old Name");
        var svc = NewService(db, TestConfig(), MockUserManager(db));

        var resp = await svc.UpdateOrganizationAsync(orgId,
            new Isas.AuthService.DTOs.UpdateOrgRequest { Name = "New Name", TaxCode = "999" });

        Assert.Equal("New Name", resp.Name);
        Assert.Equal("999", resp.TaxCode);

        using var verify = testDb.NewContext();
        var org = verify.Organizations.Single(o => o.Id == orgId);
        Assert.Equal("New Name", org.Name);
        Assert.Equal("999", org.TaxCode);
    }

    [Fact]
    public async Task UpdateOrganization_PartialName_KeepsExistingTaxCode()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var org = new Organization { Id = Guid.NewGuid(), Name = "Acme", TaxCode = "KEEP", CreatedAt = DateTime.UtcNow };
        db.Organizations.Add(org);
        db.SaveChanges();
        var svc = NewService(db, TestConfig(), MockUserManager(db));

        // Chỉ gửi Name → TaxCode (null trong request) giữ nguyên.
        var resp = await svc.UpdateOrganizationAsync(org.Id,
            new Isas.AuthService.DTOs.UpdateOrgRequest { Name = "Acme 2" });

        Assert.Equal("Acme 2", resp.Name);
        Assert.Equal("KEEP", resp.TaxCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────
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

        mgr.Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .Returns<string>(e => Task.FromResult(
                db.Users.FirstOrDefault(u => u.Email!.ToLower() == e.ToLower())));

        // CreateAsync KHÔNG mật khẩu (passwordless) — persist user thật vào SQLite.
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
