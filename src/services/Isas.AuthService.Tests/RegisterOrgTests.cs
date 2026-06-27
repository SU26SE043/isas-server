using System.IdentityModel.Tokens.Jwt;
using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

public class RegisterOrgTests
{
    // A3 (tasks.md): POST /auth/register-org → org tạo, user = OrgAdmin.
    // Mock Identity managers (CreateAsync persist user vào SQLite để FK org_members hợp lệ),
    // AuthDbContext + JwtService chạy thật → verify hiệu ứng DB + token.
    [Fact]
    public async Task RegisterOrg_CreatesOrganization_AndMakesUserOrgAdmin()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();

        var userManager = MockUserManager(db);
        var roleManager = MockRoleManager();
        var signInManager = MockSignInManager(userManager.Object);
        var sut = new Isas.AuthService.Services.AuthService(db, new JwtService(config),
            userManager.Object, roleManager.Object, config, signInManager.Object);

        var resp = await sut.RegisterOrgAsync(new RegisterOrgRequest
        {
            Email = "owner@acme.test",
            Password = "Passw0rd!",
            FullName = "Owner",
            OrgName = "Acme Corp",
            TaxCode = "0101234567"
        });

        using var verify = testDb.NewContext();

        // org tạo
        var org = verify.Organizations.Single();
        Assert.Equal("Acme Corp", org.Name);
        Assert.Equal("0101234567", org.TaxCode);

        // user = OrgAdmin + được gán role Employer
        var member = verify.OrgMembers.Single();
        Assert.Equal(org.Id, member.OrgId);
        Assert.Equal(OrgRole.OrgAdmin, member.OrgRole);
        userManager.Verify(m => m.AddToRoleAsync(It.IsAny<User>(), "Employer"), Times.Once);

        // token trả về mang org_id + org_role (A2 wired)
        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(resp.AccessToken);
        Assert.Equal(org.Id.ToString(), decoded.Claims.Single(c => c.Type == "org_id").Value);
        Assert.Equal("OrgAdmin", decoded.Claims.Single(c => c.Type == "org_role").Value);
    }

    private static IConfiguration TestConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "isas-test-signing-key-0123456789-abcdef",
            ["Jwt:Issuer"] = "isas-test",
            ["Jwt:Audience"] = "isas-test",
            ["Jwt:AccessTokenMinutes"] = "15",
            ["Jwt:RefreshTokenDays"] = "7"
        }).Build();

    private static Mock<UserManager<User>> MockUserManager(AuthDbContext db)
    {
        var mgr = new Mock<UserManager<User>>(
            Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        // CreateAsync persist user thật để org_members FK→users hợp lệ trên SQLite
        mgr.Setup(m => m.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
            .Returns<User, string>((u, _) =>
            {
                db.Users.Add(u);
                db.SaveChanges();
                return Task.FromResult(IdentityResult.Success);
            });
        mgr.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);
        mgr.Setup(m => m.GetRolesAsync(It.IsAny<User>()))
            .ReturnsAsync(new List<string> { "Employer" });
        return mgr;
    }

    private static Mock<RoleManager<Role>> MockRoleManager()
    {
        var mgr = new Mock<RoleManager<Role>>(
            Mock.Of<IRoleStore<Role>>(), null!, null!, null!, null!);
        mgr.Setup(m => m.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        return mgr;
    }

    private static Mock<SignInManager<User>> MockSignInManager(UserManager<User> userManager) =>
        new(userManager, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);
}
