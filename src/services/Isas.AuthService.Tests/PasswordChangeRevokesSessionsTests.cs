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
using Microsoft.Extensions.Logging;
using Moq;
using static Isas.AuthService.DTOs.ForgotPasswordDtos;

namespace Isas.AuthService.Tests;

/// <summary>
/// Q3 — đổi mật khẩu phải THU HỒI MỌI PHIÊN.
///
/// Đo được trên deploy trước khi vá: <c>change-password</c> trả 204 và mật khẩu đổi thật (đăng nhập
/// bằng mật khẩu cũ → 401), nhưng refresh token lấy TRƯỚC lúc đổi vẫn refresh được → 200, và access
/// token nhận về gọi <c>/auth/me</c> → 200. Đối chứng: <c>logout</c> làm refresh chết ngay ⇒ cơ chế
/// thu hồi CÓ và chạy đúng, chỉ hai đường mật khẩu không gọi tới nó. Vì refresh xoay vòng cấp token
/// 7 ngày MỚI mỗi lần, truy cập của kẻ chiếm tài khoản GIA HẠN VÔ HẠN — nạn nhân làm đúng thao tác
/// được dạy mà không đuổi được ai.
///
/// Test chạy <see cref="AuthController"/> THẬT + <see cref="Isas.AuthService.Services.AuthService"/>
/// THẬT trên SQLite, nên khẳng định là hàng <c>refresh_tokens</c> trong DB, không phải "mock đã được
/// gọi". Ca đối chứng (mật khẩu cũ SAI / OTP SAI) khoá THỨ TỰ: thu hồi phải nằm SAU khi đổi thành
/// công — thu hồi trước sẽ đá người gõ nhầm ra khỏi mọi phiên.
/// </summary>
public class PasswordChangeRevokesSessionsTests
{
    private const string Email = "victim@acme.test";
    private const string BystanderEmail = "bystander@acme.test";
    private const string OldPassword = "OldPassw0rd!";
    private const string NewPassword = "NewPassw0rd!";

    // ── change-password ────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_Succeeds_RevokesEveryRefreshTokenOfThatUser()
    {
        using var h = new Harness();
        h.SeedRefreshToken(h.UserId, "hash-tab-a");
        h.SeedRefreshToken(h.UserId, "hash-tab-b");

        var result = await h.ChangePasswordAsync(OldPassword, NewPassword);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(NewPassword, h.CurrentPassword);
        // Mỗi tab giữ refresh token riêng — sót một cái là phiên của kẻ chiếm tài khoản sống tiếp.
        Assert.All(h.TokensOf(h.UserId), t => Assert.True(t.IsRevoked));
    }

    [Fact]
    public async Task ChangePassword_WrongOldPassword_Returns400_AndKeepsSessionsAlive()
    {
        using var h = new Harness();
        h.SeedRefreshToken(h.UserId, "hash-tab-a");

        var result = await h.ChangePasswordAsync("WrongOldPassw0rd!", NewPassword);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(OldPassword, h.CurrentPassword);
        // Gõ nhầm mật khẩu cũ là chuyện thường ngày: không được vì thế mà mất hết phiên đang mở.
        Assert.All(h.TokensOf(h.UserId), t => Assert.False(t.IsRevoked));
    }

    [Fact]
    public async Task ChangePassword_DoesNotTouchOtherUsersSessions()
    {
        using var h = new Harness();
        h.SeedRefreshToken(h.UserId, "hash-mine");
        h.SeedRefreshToken(h.BystanderId, "hash-theirs");

        Assert.IsType<NoContentResult>(await h.ChangePasswordAsync(OldPassword, NewPassword));

        Assert.True(h.TokensOf(h.UserId).Single().IsRevoked);
        Assert.False(h.TokensOf(h.BystanderId).Single().IsRevoked);
    }

    // ── reset-password (OTP) ───────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_Succeeds_RevokesEveryRefreshTokenOfThatUser()
    {
        using var h = new Harness();
        h.SeedRefreshToken(h.UserId, "hash-tab-a");
        h.SeedRefreshToken(h.UserId, "hash-tab-b");
        var otp = await h.RequestOtpAsync();
        await h.VerifyOtpAsync(otp);

        var result = await h.ResetPasswordAsync(otp);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(NewPassword, h.CurrentPassword);
        // Đây là đúng đường "tôi mất quyền kiểm soát tài khoản, xin lại bằng email" — nếu nó không
        // đuổi được phiên đang chạy thì việc đặt lại mật khẩu chẳng cứu được gì.
        Assert.All(h.TokensOf(h.UserId), t => Assert.True(t.IsRevoked));
    }

    [Fact]
    public async Task ResetPassword_WrongOtp_Returns400_AndKeepsSessionsAlive()
    {
        using var h = new Harness();
        h.SeedRefreshToken(h.UserId, "hash-tab-a");
        var otp = await h.RequestOtpAsync();
        await h.VerifyOtpAsync(otp);

        var result = await h.ResetPasswordAsync(otp == "000000" ? "111111" : "000000");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(OldPassword, h.CurrentPassword);
        // Người lạ biết email nạn nhân không được phép đá nạn nhân ra khỏi phiên (từ chối dịch vụ rẻ tiền).
        Assert.All(h.TokensOf(h.UserId), t => Assert.False(t.IsRevoked));
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    /// <summary>
    /// AuthController thật + AuthService thật (SQLite) + UserManager mock giữ mật khẩu và kho token
    /// OTP trong bộ nhớ. Verify đọc qua context MỚI vì thu hồi chạy bằng <c>ExecuteUpdateAsync</c>
    /// (bỏ qua change tracker) — đọc lại từ context cũ sẽ thấy giá trị cũ.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly AuthTestDb _testDb;
        private readonly Dictionary<string, string> _tokens = new();
        private readonly User _user;
        private readonly User _bystander;

        public AuthController Controller { get; }
        public string CurrentPassword { get; private set; } = OldPassword;
        public Guid UserId => _user.Id;
        public Guid BystanderId => _bystander.Id;

        public Harness()
        {
            _testDb = new AuthTestDb();
            _user = SeedUser(Email);
            _bystander = SeedUser(BystanderEmail);

            var mgr = new Mock<UserManager<User>>(
                Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);

            mgr.Setup(m => m.FindByIdAsync(It.IsAny<string>()))
                .Returns<string>(id => Task.FromResult(
                    Guid.TryParse(id, out var g) ? FindUser(u => u.Id == g) : null));
            mgr.Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
                .Returns<string>(e => Task.FromResult(
                    FindUser(u => string.Equals(u.Email, e, StringComparison.OrdinalIgnoreCase))));

            // Mật khẩu thật sự được kiểm: mật khẩu cũ sai → Identity từ chối, đúng như production.
            mgr.Setup(m => m.ChangePasswordAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<User, string, string>((_, oldPwd, newPwd) =>
                {
                    if (oldPwd != CurrentPassword)
                        return Task.FromResult(IdentityResult.Failed(
                            new IdentityError { Description = "Incorrect password." }));
                    CurrentPassword = newPwd;
                    return Task.FromResult(IdentityResult.Success);
                });

            mgr.Setup(m => m.GetAuthenticationTokenAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<User, string, string>((_, _, name) =>
                    Task.FromResult(_tokens.TryGetValue(name, out var v) ? v : null));
            mgr.Setup(m => m.SetAuthenticationTokenAsync(
                    It.IsAny<User>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<User, string, string, string>((_, _, name, value) =>
                {
                    _tokens[name] = value;
                    return Task.FromResult(IdentityResult.Success);
                });
            mgr.Setup(m => m.RemoveAuthenticationTokenAsync(
                    It.IsAny<User>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns<User, string, string>((_, _, name) =>
                {
                    _tokens.Remove(name);
                    return Task.FromResult(IdentityResult.Success);
                });

            mgr.Setup(m => m.GeneratePasswordResetTokenAsync(It.IsAny<User>()))
                .ReturnsAsync("identity-reset-token");
            mgr.Setup(m => m.ResetPasswordAsync(It.IsAny<User>(), "identity-reset-token", It.IsAny<string>()))
                .Returns<User, string, string>((_, _, pwd) =>
                {
                    CurrentPassword = pwd;
                    return Task.FromResult(IdentityResult.Success);
                });
            mgr.Setup(m => m.GetRolesAsync(It.IsAny<User>())).ReturnsAsync(new List<string> { "Candidate" });

            var config = TestConfig();
            var roleManager = new Mock<RoleManager<Role>>(
                Mock.Of<IRoleStore<Role>>(), null!, null!, null!, null!);
            roleManager.Setup(m => m.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
            var signIn = new Mock<SignInManager<User>>(mgr.Object, Mock.Of<IHttpContextAccessor>(),
                Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);

            // AuthService THẬT: khẳng định của test là hàng trong DB, không phải "mock đã được gọi".
            var authService = new Isas.AuthService.Services.AuthService(
                _testDb.Db, new JwtService(config), mgr.Object, roleManager.Object, config, signIn.Object);

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _user.Id.ToString()),
                 new Claim(ClaimTypes.Role, "Candidate")], "test");

            Controller = new AuthController(
                authService,
                mgr.Object,
                signIn.Object,
                Mock.Of<IEmailSender>(),
                Mock.Of<IGoogleLoginRedirects>(),
                Mock.Of<IGoogleAuthCodeStore>(),
                Mock.Of<ILogger<AuthController>>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
                }
            };
        }

        public Task<IActionResult> ChangePasswordAsync(string oldPassword, string newPassword) =>
            Controller.ChangePasswordAsync(new ChangePasswordRequest
            {
                OldPassword = oldPassword,
                NewPassword = newPassword
            });

        /// <summary>Chạy forgot-password THẬT rồi đọc mã ra khỏi kho — test không tự bịa OTP.</summary>
        public async Task<string> RequestOtpAsync()
        {
            await Controller.ForgotPassword(new ForgotPasswordDto { Email = Email });
            return _tokens["OTPCode"];
        }

        public Task<IActionResult> VerifyOtpAsync(string otp) =>
            Controller.VerifyOtp(new VerifyOtpDto { Email = Email, Otp = otp });

        public Task<IActionResult> ResetPasswordAsync(string otp) =>
            Controller.ResetPassword(new ResetPasswordDto
            {
                Email = Email,
                Otp = otp,
                NewPassword = NewPassword
            });

        public void SeedRefreshToken(Guid userId, string token)
        {
            _testDb.Db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Token = token,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow
            });
            _testDb.Db.SaveChanges();
        }

        public IReadOnlyList<RefreshToken> TokensOf(Guid userId)
        {
            using var verify = _testDb.NewContext();
            return verify.RefreshTokens.Where(t => t.UserId == userId).ToList();
        }

        private User? FindUser(Func<User, bool> predicate) =>
            new[] { _user, _bystander }.FirstOrDefault(predicate);

        private User SeedUser(string email)
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
            _testDb.Db.Users.Add(user);
            _testDb.Db.SaveChanges();
            return user;
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

        public void Dispose() => _testDb.Dispose();
    }
}
