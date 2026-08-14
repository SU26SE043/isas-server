using System.Security.Claims;
using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// <c>POST /auth/google/id-token</c> — đăng nhập Google NATIVE (app mobile gửi thẳng ID token, không
/// có vòng redirect trình duyệt). Test ở tầng controller với <see cref="AuthService"/> THẬT, chỉ giả
/// lập bước verify token: cái cần khoá là <b>token đã verify dẫn tới đúng account nào</b>, không phải
/// việc thư viện Google có kiểm chữ ký đúng không.
/// </summary>
public class GoogleIdTokenLoginTests
{
    private const string Sub = "108234567890123456789";      // 'sub' Google — số, KHÔNG phải email
    private const string Email = "candidate@gmail.test";

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Bất biến QUAN TRỌNG NHẤT của cả tính năng.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Đăng nhập web (đường OAuth) rồi đăng nhập mobile (đường ID token) với CÙNG account Google phải
    /// ra CÙNG một user — vì cả hai dùng <c>sub</c> làm <c>ProviderKey</c>.
    /// <para>
    /// ⚠ Chỉ assert "cùng userId" là KHÔNG đủ để bắt lỗi: nếu <c>ProviderKey</c> bị đổi thành email
    /// thì lần đăng nhập mobile tra không ra liên kết, rơi xuống nhánh "email đã tồn tại" và… vẫn ra
    /// đúng user đó. Cái phân biệt được là <c>AddLoginAsync</c> bị gọi LẦN HAI (gắn thêm một liên kết
    /// thứ hai cho cùng một người) — nên khoá cả số lần gọi lẫn khoá đã lưu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DangNhapWebRoiMobile_CungAccountGoogle_RaCungUser_VaKhongGanLienKetThuHai()
    {
        using var testDb = new AuthTestDb();
        var logins = new Dictionary<(string Provider, string Key), User>();
        var userManager = MockUserManager(testDb.Db, logins);
        var service = NewService(testDb.Db, userManager);

        // Chặng 1 — đăng nhập WEB: ExternalLoginInfo do SignInManager dựng từ cookie handler Google.
        var web = await service.LoginGoogleAsync(WebExternalLoginInfo(Email, Sub));

        // Chặng 2 — đăng nhập MOBILE: cùng account Google, nhưng đi qua endpoint ID token.
        var mobile = await Controller(service, userManager, Verifier(Payload()))
            .LoginWithGoogleIdToken(new GoogleIdTokenRequest { IdToken = "any-token" });

        var auth = Assert.IsType<AuthResponse>(Assert.IsType<OkObjectResult>(mobile.Result).Value);
        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));

        using var verify = testDb.NewContext();
        Assert.Single(verify.Users);                                    // KHÔNG sinh user thứ hai
        var userId = verify.Users.Single().Id;
        Assert.All(verify.RefreshTokens.ToList(), t => Assert.Equal(userId, t.UserId));
        Assert.Equal(2, verify.RefreshTokens.Count());                  // 2 phiên, cùng 1 người

        // Liên kết external chỉ được gắn MỘT lần (ở lần web). Mobile phải TRA RA nó, không gắn thêm.
        userManager.Verify(m => m.AddLoginAsync(It.IsAny<User>(), It.IsAny<UserLoginInfo>()), Times.Once);
        userManager.Verify(m => m.CreateAsync(It.IsAny<User>()), Times.Once);

        // Và khoá lưu trong user_logins là ('Google', 'sub') — không phải email, không phải tên provider khác.
        Assert.True(logins.ContainsKey((WebHandlerProvider, Sub)));
        Assert.False(logins.ContainsKey((WebHandlerProvider, Email)));

        Assert.False(string.IsNullOrWhiteSpace(web.AccessToken));
    }

    /// <summary>
    /// Tên provider của đường mobile phải TRÙNG KHÍT chuỗi mà handler OAuth của đường web dùng.
    /// Lệch một ký tự là <c>user_logins</c> có hai họ khoá khác nhau cho cùng một người: web tra không
    /// ra bản ghi mobile và ngược lại ⇒ mỗi người dùng dần có hai liên kết, im lặng.
    /// </summary>
    [Fact]
    public void TenProvider_TrungKhitVoiHandlerOAuthDuongWeb()
    {
        Assert.Equal(WebHandlerProvider, GoogleExternalLogin.Provider);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Đường thành công + các cửa từ chối.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TokenHopLe_UserChuaTonTai_TaoUserCandidate_VaTraToken()
    {
        using var testDb = new AuthTestDb();
        var userManager = MockUserManager(testDb.Db, []);

        var result = await Controller(NewService(testDb.Db, userManager), userManager, Verifier(Payload()))
            .LoginWithGoogleIdToken(new GoogleIdTokenRequest { IdToken = "any-token" });

        var auth = Assert.IsType<AuthResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(auth.RefreshToken));

        using var verify = testDb.NewContext();
        var user = Assert.Single(verify.Users);
        Assert.Equal(Email, user.Email);
        userManager.Verify(m => m.AddToRoleAsync(It.IsAny<User>(), "Candidate"), Times.Once);

        // FR18 — lượt đăng nhập được đếm, và ghi đúng phương thức Google (không phải Password).
        Assert.Equal(LoginMethod.Google, Assert.Single(verify.LoginEvents).Method);
    }

    /// <summary>
    /// 🔴 Cửa chặn chiếm account. <c>LoginGoogleAsync</c> gắn external login vào account MẬT KHẨU sẵn
    /// có khi trùng email; token do client gửi lên nên nếu bỏ qua <c>email_verified</c> thì một account
    /// Google mang địa chỉ của người khác sẽ chiếm được account ISAS đó.
    /// </summary>
    [Fact]
    public async Task EmailChuaXacMinh_Tra401_VaKhongDungToiAccountSanCo()
    {
        using var testDb = new AuthTestDb();
        var existing = SeedUser(testDb.Db, Email);
        var userManager = MockUserManager(testDb.Db, []);

        var result = await Controller(NewService(testDb.Db, userManager), userManager,
                Verifier(Payload(emailVerified: false)))
            .LoginWithGoogleIdToken(new GoogleIdTokenRequest { IdToken = "any-token" });

        Assert.IsType<UnauthorizedObjectResult>(result.Result);

        // Không liên kết, không phát phiên nào cho account của người ta.
        userManager.Verify(m => m.AddLoginAsync(It.IsAny<User>(), It.IsAny<UserLoginInfo>()), Times.Never);
        using var verify = testDb.NewContext();
        Assert.Empty(verify.RefreshTokens);
        Assert.Single(verify.Users);
        Assert.Equal(existing.Id, verify.Users.Single().Id);
    }

    [Fact]
    public async Task TokenKhongHopLe_Tra401_VaKhongTaoUser()
    {
        using var testDb = new AuthTestDb();
        var userManager = MockUserManager(testDb.Db, []);

        var verifier = new Mock<IGoogleIdTokenVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidGoogleIdTokenException("chữ ký sai"));

        var result = await Controller(NewService(testDb.Db, userManager), userManager, verifier.Object)
            .LoginWithGoogleIdToken(new GoogleIdTokenRequest { IdToken = "rác" });

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        using var verify = testDb.NewContext();
        Assert.Empty(verify.Users);
        Assert.Empty(verify.RefreshTokens);
    }

    /// <summary>F20 — 403 chứ không 401: danh tính đúng, cái bị từ chối là quyền dùng hệ thống.</summary>
    [Fact]
    public async Task UserBiDinhChi_Tra403()
    {
        using var testDb = new AuthTestDb();
        var banned = SeedUser(testDb.Db, Email, bannedAt: DateTime.UtcNow);
        var userManager = MockUserManager(testDb.Db, new Dictionary<(string, string), User>
        {
            [(WebHandlerProvider, Sub)] = banned
        });

        var result = await Controller(NewService(testDb.Db, userManager), userManager, Verifier(Payload()))
            .LoginWithGoogleIdToken(new GoogleIdTokenRequest { IdToken = "any-token" });

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        using var verify = testDb.NewContext();
        Assert.Empty(verify.RefreshTokens);      // bị chặn TRƯỚC khi phát phiên
    }

    [Fact]
    public async Task ThieuEmailTrongToken_Tra401()
    {
        using var testDb = new AuthTestDb();
        var userManager = MockUserManager(testDb.Db, []);

        var result = await Controller(NewService(testDb.Db, userManager), userManager,
                Verifier(new GoogleIdTokenPayload(Sub, null, true, "Ứng viên")))
            .LoginWithGoogleIdToken(new GoogleIdTokenRequest { IdToken = "any-token" });

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        using var verify = testDb.NewContext();
        Assert.Empty(verify.Users);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Allowlist 'aud' — fail-closed.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Audiences_LayTuIdTokenAudiences_KhiCoKhai()
    {
        var audiences = GoogleIdTokenVerifier.ResolveAudiences(Config(
            clientId: "web-client-id",
            audiences: ["mobile-aud-1", "mobile-aud-2"]));

        Assert.Equal(["mobile-aud-1", "mobile-aud-2"], audiences);
    }

    /// <summary>App xin ID token kèm <c>serverClientId</c> = web client ID ⇒ mặc định đã đúng.</summary>
    [Fact]
    public void Audiences_RoiVeClientId_KhiKhongKhaiRieng()
    {
        Assert.Equal(["web-client-id"],
            GoogleIdTokenVerifier.ResolveAudiences(Config(clientId: "web-client-id", audiences: [])));
    }

    /// <summary>
    /// 🔴 Trống CẢ HAI thì phải NÉM, tuyệt đối không trả danh sách rỗng:
    /// <c>ValidationSettings.Audience</c> rỗng = thư viện Google BỎ QUA kiểm tra <c>aud</c> ⇒ mọi Google
    /// ID token trên đời (kể cả token lấy từ project Google của kẻ tấn công) đều đăng nhập được.
    /// </summary>
    [Fact]
    public void Audiences_TrongCaHai_ThiNem_ChuKhongMoToang()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GoogleIdTokenVerifier.ResolveAudiences(Config(clientId: "", audiences: [])));
    }

    [Fact]
    public void Audiences_BoQuaDongRong_VaTrungLap()
    {
        var audiences = GoogleIdTokenVerifier.ResolveAudiences(Config(
            clientId: "web-client-id",
            audiences: ["  aud-1  ", "", "   ", "aud-1"]));

        Assert.Equal(["aud-1"], audiences);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Helper
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private static GoogleIdTokenPayload Payload(bool emailVerified = true) =>
        new(Sub, Email, emailVerified, "Ứng viên");

    private static IGoogleIdTokenVerifier Verifier(GoogleIdTokenPayload payload)
    {
        var mock = new Mock<IGoogleIdTokenVerifier>();
        mock.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(payload);
        return mock.Object;
    }

    /// <summary>
    /// ExternalLoginInfo đúng như SignInManager dựng ở đường web (ProviderKey = 'sub').
    /// <para>
    /// ⚠ Chuỗi <c>"Google"</c> ở đây CỐ Ý gõ tay, không dùng <see cref="GoogleExternalLogin.Provider"/>:
    /// đây là vế mô phỏng ĐƯỜNG WEB, mà đường web lấy tên provider từ scheme của handler Google
    /// (<c>Challenge(properties, "Google")</c>) chứ không đọc hằng số của ta. Đọc chung hằng số thì hai
    /// vế luôn khớp nhau kể cả khi hằng số sai — test sẽ mù đúng thứ nó sinh ra để canh.
    /// </para>
    /// </summary>
    private const string WebHandlerProvider = "Google";

    private static ExternalLoginInfo WebExternalLoginInfo(string email, string sub)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, sub), new Claim(ClaimTypes.Email, email),
             new Claim(ClaimTypes.Name, "Ứng viên")],
            WebHandlerProvider);

        return new ExternalLoginInfo(new ClaimsPrincipal(identity),
            WebHandlerProvider, sub, WebHandlerProvider);
    }

    private static User SeedUser(AuthDbContext db, string email, DateTime? bannedAt = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            FullName = "Người dùng sẵn có",
            BannedAt = bannedAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static AuthController Controller(
        Isas.AuthService.Services.AuthService service,
        Mock<UserManager<User>> userManager,
        IGoogleIdTokenVerifier verifier)
    {
        var signInManager = new Mock<SignInManager<User>>(userManager.Object,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);

        return new AuthController(
            service,
            userManager.Object,
            signInManager.Object,
            Mock.Of<IEmailSender>(),
            Mock.Of<IGoogleLoginRedirects>(),
            Mock.Of<IGoogleAuthCodeStore>(),
            verifier,
            NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static Isas.AuthService.Services.AuthService NewService(
        AuthDbContext db, Mock<UserManager<User>> userManager) =>
        new(db, new JwtService(TestConfig()), userManager.Object, MockRoleManager().Object,
            TestConfig(), MockSignInManager(userManager.Object).Object);

    /// <summary>
    /// UserManager giả có <b>kho user_logins thật</b> (dictionary): <c>AddLoginAsync</c> ghi vào,
    /// <c>FindByLoginAsync</c> đọc ra. Không có nó thì test "web rồi mobile" chỉ chạy trên stub trả
    /// null cứng và không chứng minh được gì về việc tra ra liên kết cũ.
    /// </summary>
    private static Mock<UserManager<User>> MockUserManager(
        AuthDbContext db, Dictionary<(string Provider, string Key), User> logins)
    {
        var mgr = new Mock<UserManager<User>>(
            Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);

        mgr.Setup(m => m.CreateAsync(It.IsAny<User>()))
            .Returns<User>(u =>
            {
                db.Users.Add(u);
                db.SaveChanges();
                return Task.FromResult(IdentityResult.Success);
            });

        mgr.Setup(m => m.AddLoginAsync(It.IsAny<User>(), It.IsAny<UserLoginInfo>()))
            .Returns<User, UserLoginInfo>((u, info) =>
            {
                logins[(info.LoginProvider, info.ProviderKey)] = u;
                return Task.FromResult(IdentityResult.Success);
            });

        mgr.Setup(m => m.FindByLoginAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((provider, key) =>
                Task.FromResult(logins.TryGetValue((provider, key), out var u) ? u : null));

        mgr.Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .Returns<string>(email =>
                Task.FromResult(db.Users.FirstOrDefault(u => u.Email == email)));

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

    private static IConfiguration Config(string clientId, string[] audiences)
    {
        var values = new Dictionary<string, string?>
        {
            [GoogleIdTokenVerifier.ClientIdKey] = clientId
        };
        for (var i = 0; i < audiences.Length; i++)
            values[$"{GoogleIdTokenVerifier.AudiencesKey}:{i}"] = audiences[i];

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
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
}
