using System.IdentityModel.Tokens.Jwt;
using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

public class RegisterTests
{
    // AUTH-1 (P0 B2C): POST /auth/register phải trả AuthResponse {accessToken, refreshToken, expiresAt}
    // như Login — frontend nhận token ngay khi đăng ký (trước đây trả string "User ID: {guid}" → không có token).
    // Mock Identity managers (CreateAsync persist user vào SQLite → FK refresh_tokens→users hợp lệ),
    // AuthDbContext + JwtService chạy thật → verify token thật + persist refresh token.
    [Fact]
    public async Task Register_ReturnsAuthResponse_WithTokensAndFutureExpiry()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();

        var userManager = MockUserManager(db);
        var roleManager = MockRoleManager();
        var signInManager = MockSignInManager(userManager.Object);
        var sut = new Isas.AuthService.Services.AuthService(db, new JwtService(config),
            userManager.Object, roleManager.Object, config, signInManager.Object);

        var before = DateTime.UtcNow;
        var resp = await sut.RegisterAsync(new RegisterRequest
        {
            Email = "candidate@acme.test",
            Password = "Passw0rd!",
            FullName = "Candidate"
        });

        // AuthResponse mang token (không còn string id).
        Assert.NotNull(resp);
        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(resp.RefreshToken));
        Assert.True(resp.ExpiresAt > before);

        // accessToken là JWT hợp lệ (đọc được).
        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(resp.AccessToken);
        Assert.NotEmpty(decoded.Claims);

        // refresh token đã persist (hash) → refresh sau này dùng được.
        using var verify = testDb.NewContext();
        Assert.Equal(1, verify.RefreshTokens.Count());

        // user được gán role Candidate (AUTH-1).
        userManager.Verify(m => m.AddToRoleAsync(It.IsAny<User>(), "Candidate"), Times.Once);
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
        // CreateAsync persist user thật để refresh_tokens FK→users hợp lệ trên SQLite
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
            .ReturnsAsync(new List<string> { "Candidate" });
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
