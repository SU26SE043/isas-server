using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// D2 — provision Candidate nhẹ (internal). AuthService thật + AuthDbContext SQLite;
/// UserManager mock (CreateAsync persist vào SQLite, FindByEmailAsync đọc lại) → verify idempotency.
/// </summary>
public class ProvisionCandidateTests
{
    [Fact]
    public async Task NewEmail_TaoCandidate_TraCandidateId_VaJwtCandidate()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();
        var svc = NewService(db, config);

        var resp = await svc.ProvisionCandidateAsync("newbie@acme.test", "Newbie");

        // account tạo với role Candidate
        using var verify = testDb.NewContext();
        var user = verify.Users.Single();
        Assert.Equal("newbie@acme.test", user.Email);
        Assert.Equal(user.Id, resp.CandidateId);

        // JWT mang role Candidate + sub = candidateId
        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(resp.AccessToken);
        Assert.Equal(user.Id.ToString(), decoded.Claims.Single(c => c.Type == "sub").Value);
        Assert.Contains(decoded.Claims, c => c.Type == ClaimTypes.Role && c.Value == "Candidate");
    }

    [Fact]
    public async Task ExistingEmail_DungLaiAccount_CungCandidateId_KhongTaoTrung()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();
        var svc = NewService(db, config);

        var first = await svc.ProvisionCandidateAsync("dup@acme.test", null);
        var second = await svc.ProvisionCandidateAsync("dup@acme.test", "Khác tên");

        Assert.Equal(first.CandidateId, second.CandidateId);

        using var verify = testDb.NewContext();
        Assert.Single(verify.Users);   // không tạo account thứ 2
    }

    [Fact]
    public async Task Controller_SaiInternalToken_Tra401()
    {
        var config = TestConfig();
        var controller = new InternalAuthController(
            Mock.Of<IAuthService>(), config, NullLogger<InternalAuthController>.Instance);

        var result = await controller.ProvisionCandidate(
            new ProvisionCandidateRequest { Email = "x@acme.test" }, token: "wrong-token", default);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static Isas.AuthService.Services.AuthService NewService(AuthDbContext db, IConfiguration config)
    {
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

        // FindByEmailAsync đọc lại từ SQLite (case-insensitive) → mô phỏng create-or-get.
        mgr.Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .Returns<string>(e => Task.FromResult(
                db.Users.FirstOrDefault(u => u.Email!.ToLower() == e.ToLower())));

        // CreateAsync KHÔNG mật khẩu (magic-link) — persist user thật vào SQLite.
        mgr.Setup(m => m.CreateAsync(It.IsAny<User>()))
            .Returns<User>(u => { db.Users.Add(u); db.SaveChanges(); return Task.FromResult(IdentityResult.Success); });
        mgr.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);
        mgr.Setup(m => m.GetRolesAsync(It.IsAny<User>()))
            .ReturnsAsync(new List<string> { "Candidate" });
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
