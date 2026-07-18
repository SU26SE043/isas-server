using System.Security.Claims;
using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

public class GoogleLoginTests
{
    // Account linking: email Google trùng account MẬT KHẨU sẵn có → liên kết external login vào
    // account đó rồi đăng nhập. Trước đây rơi thẳng xuống CreateAsync → vi phạm UNIQUE email →
    // exception → 500 và user đã đăng ký bằng mật khẩu vĩnh viễn không dùng được Google.
    [Fact]
    public async Task LoginGoogle_EmailTrungAccountSanCo_LienKetChuKhongTaoUserMoi()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();

        var existing = new User
        {
            Id = Guid.NewGuid(),
            UserName = "candidate@acme.test",
            Email = "candidate@acme.test",
            FullName = "Candidate",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(existing);
        db.SaveChanges();

        var userManager = MockUserManager(db);
        userManager.Setup(m => m.FindByLoginAsync("Google", "google-sub-1"))
            .ReturnsAsync((User?)null);                 // chưa liên kết
        userManager.Setup(m => m.FindByEmailAsync("candidate@acme.test"))
            .ReturnsAsync(existing);                    // nhưng email đã có account

        var sut = new Isas.AuthService.Services.AuthService(db, new JwtService(config),
            userManager.Object, MockRoleManager().Object, config,
            MockSignInManager(userManager.Object).Object);

        var resp = await sut.LoginGoogleAsync(ExternalInfo("candidate@acme.test", "google-sub-1"));

        // Đăng nhập được vào ĐÚNG account cũ (token thật, refresh token persist theo user cũ).
        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(resp.RefreshToken));
        using var verify = testDb.NewContext();
        Assert.Equal(existing.Id, verify.RefreshTokens.Single().UserId);

        // Liên kết vào account cũ, KHÔNG tạo user thứ hai.
        userManager.Verify(m => m.AddLoginAsync(existing, It.IsAny<UserLoginInfo>()), Times.Once);
        userManager.Verify(m => m.CreateAsync(It.IsAny<User>()), Times.Never);
        Assert.Equal(1, verify.Users.Count());
    }

    // Lần đăng nhập Google THỨ HAI trở đi: external login đã liên kết → dùng lại account, không
    // đụng CreateAsync/AddLoginAsync nữa.
    [Fact]
    public async Task LoginGoogle_ExternalLoginDaLienKet_DungLaiAccount()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();

        var linked = new User
        {
            Id = Guid.NewGuid(),
            UserName = "returning@acme.test",
            Email = "returning@acme.test",
            FullName = "Returning",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(linked);
        db.SaveChanges();

        var userManager = MockUserManager(db);
        userManager.Setup(m => m.FindByLoginAsync("Google", "google-sub-2")).ReturnsAsync(linked);

        var sut = new Isas.AuthService.Services.AuthService(db, new JwtService(config),
            userManager.Object, MockRoleManager().Object, config,
            MockSignInManager(userManager.Object).Object);

        var resp = await sut.LoginGoogleAsync(ExternalInfo("returning@acme.test", "google-sub-2"));

        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));
        userManager.Verify(m => m.CreateAsync(It.IsAny<User>()), Times.Never);
        userManager.Verify(m => m.AddLoginAsync(It.IsAny<User>(), It.IsAny<UserLoginInfo>()), Times.Never);
    }

    // Chưa có gì → tạo account passwordless + role Candidate (AUTH-1), có gắn external login.
    [Fact]
    public async Task LoginGoogle_ChuaCoAccount_TaoMoiVoiRoleCandidate()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var config = TestConfig();

        var userManager = MockUserManager(db);
        userManager.Setup(m => m.FindByLoginAsync("Google", "google-sub-3")).ReturnsAsync((User?)null);
        userManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);

        var sut = new Isas.AuthService.Services.AuthService(db, new JwtService(config),
            userManager.Object, MockRoleManager().Object, config,
            MockSignInManager(userManager.Object).Object);

        var resp = await sut.LoginGoogleAsync(ExternalInfo("newbie@acme.test", "google-sub-3"));

        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));
        userManager.Verify(m => m.CreateAsync(It.IsAny<User>()), Times.Once);
        userManager.Verify(m => m.AddLoginAsync(It.IsAny<User>(), It.IsAny<UserLoginInfo>()), Times.Once);
        userManager.Verify(m => m.AddToRoleAsync(It.IsAny<User>(), "Candidate"), Times.Once);
    }

    // Guard open-redirect: returnUrl do CLIENT truyền, nhận nguyên si = tuồn token cho site tấn công.
    // Chỉ đường dẫn tương đối mới được chấp nhận; mọi thứ trỏ ra ngoài phải bị loại (null).
    [Theory]
    [InlineData("https://evil.com/steal")]           // URL tuyệt đối
    [InlineData("//evil.com/steal")]                 // protocol-relative
    [InlineData("/\\evil.com/steal")]                // trình duyệt hiểu như "//"
    [InlineData("javascript:alert(1)")]              // scheme nguy hiểm
    [InlineData("/path:with-colon")]                 // mọi dạng có scheme
    [InlineData("/path\r\nSet-Cookie: x=1")]         // ký tự điều khiển → header injection
    [InlineData("candidate/dashboard")]              // không bắt đầu bằng "/"
    [InlineData("")]
    [InlineData(null)]
    public void SanitizeReturnUrl_LoaiMoiDichNgoaiApp(string? returnUrl)
    {
        Assert.Null(GoogleLoginRedirects.SanitizeReturnUrl(returnUrl));
    }

    [Theory]
    [InlineData("/candidate/dashboard")]
    [InlineData("/employer/campaigns?tab=active")]
    [InlineData("/")]
    public void SanitizeReturnUrl_GiuDuongDanTuongDoiHopLe(string returnUrl)
    {
        Assert.Equal(returnUrl, GoogleLoginRedirects.SanitizeReturnUrl(returnUrl));
    }

    // URL redirect chỉ mang MÃ dùng-một-lần, tuyệt đối không mang token: token đặt ở URL — kể cả
    // trong fragment — vẫn đọc được từ phía trình duyệt (location.hash, extension).
    // Base URL luôn từ config server, kể cả khi client cố nhét returnUrl độc.
    [Fact]
    public void SuccessUrl_ChiMangMaKhongMangToken()
    {
        var url = Redirects().SuccessUrl("one-time-code-value", "https://evil.com/steal");

        Assert.StartsWith("https://app.isas.test/auth/google/callback?code=one-time-code-value", url);
        Assert.DoesNotContain("accessToken", url);
        Assert.DoesNotContain("refreshToken", url);
        Assert.DoesNotContain("evil.com", url);            // returnUrl độc bị loại
    }

    [Fact]
    public void SuccessUrl_GhepReturnUrlTuongDoiVaoQuery()
    {
        var url = Redirects().SuccessUrl("code-1", "/candidate/dashboard");

        Assert.Contains("&returnUrl=%2Fcandidate%2Fdashboard", url);
    }

    [Fact]
    public void FailureUrl_TraVeTrangCallbackFEKemMaLoi()
    {
        Assert.Equal("https://app.isas.test/auth/google/callback?error=login_failed",
            Redirects().FailureUrl("login_failed"));
    }

    // redirect_uri gửi Google phải là URL CÔNG KHAI qua gateway (kèm /api/v1) — gateway strip tiền tố
    // nên URL do handler tự dựng sẽ thiếu và 404 ở edge.
    [Fact]
    public void CallbackUrl_DungOriginCongKhaiKemTienToApiV1()
    {
        Assert.Equal("https://api.isas.test/api/v1/auth/login-google-callback",
            Redirects().CallbackUrl(null));
    }

    private static GoogleLoginRedirects Redirects() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Frontend:BaseUrl"] = "https://app.isas.test/",
            ["Gateway:PublicBaseUrl"] = "https://api.isas.test/api/v1"
        }).Build());

    private static ExternalLoginInfo ExternalInfo(string email, string providerKey)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, "Google User")],
            "Google");
        return new ExternalLoginInfo(new ClaimsPrincipal(identity), "Google", providerKey, "Google");
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
        // CreateAsync persist user thật để FK refresh_tokens→users hợp lệ trên SQLite.
        mgr.Setup(m => m.CreateAsync(It.IsAny<User>()))
            .Returns<User>(u =>
            {
                db.Users.Add(u);
                db.SaveChanges();
                return Task.FromResult(IdentityResult.Success);
            });
        mgr.Setup(m => m.AddLoginAsync(It.IsAny<User>(), It.IsAny<UserLoginInfo>()))
            .ReturnsAsync(IdentityResult.Success);
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
