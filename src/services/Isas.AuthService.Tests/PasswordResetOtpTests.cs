using Isas.AuthService.Controllers;
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
/// Đặt lại mật khẩu bằng OTP (DB24).
///
/// Trước khi sửa, <c>reset-password</c> đọc nhầm khoá token <c>"OTPCode"</c> (là 6 CHỮ SỐ) rồi
/// <c>DateTime.Parse</c> lên nó → FormatException với MỌI OTP hợp lệ, tức endpoint 500 vô điều kiện;
/// và nó KHÔNG hề so OTP người dùng gửi với OTP đã lưu. Sửa riêng lỗi parse mà không thêm kiểm tra
/// OTP sẽ biến 500 thành **bypass chiếm tài khoản** (biết email là đổi được mật khẩu). Bộ test này
/// khoá cả hai vế: đường đúng phải chạy, và mọi đường tắt phải bị chặn.
///
/// UserManager được mock kèm một kho token trong bộ nhớ (đúng ngữ nghĩa Set/Get/Remove của Identity)
/// nên khẳng định được cả hiệu ứng phụ: OTP bị đốt sau khi dùng, mật khẩu KHÔNG đổi khi bị từ chối.
/// </summary>
public class PasswordResetOtpTests
{
    private const string Email = "reset@acme.test";
    private const string OldPassword = "OldPassw0rd!";
    private const string NewPassword = "NewPassw0rd!";

    // ── Đường đúng ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_WithVerifiedCorrectOtp_Succeeds()
    {
        var h = new Harness();
        var otp = await h.RequestOtpAsync();

        Assert.IsType<OkObjectResult>(await h.VerifyAsync(otp));
        Assert.IsType<OkObjectResult>(await h.ResetAsync(otp));

        Assert.Equal(NewPassword, h.CurrentPassword);
    }

    [Fact]
    public async Task ForgotPassword_IssuesSixDigitOtp_AndClearsPreviousVerifiedFlag()
    {
        var h = new Harness();
        var first = await h.RequestOtpAsync();
        await h.VerifyAsync(first);
        Assert.NotNull(h.GetToken("OtpVerified"));

        // Xin mã mới phải huỷ cờ "đã verify" của lượt cũ — nếu không, mã cũ đã verify vẫn mở
        // được cửa reset dù người dùng đã yêu cầu mã khác.
        var second = await h.RequestOtpAsync();
        Assert.Null(h.GetToken("OtpVerified"));

        Assert.Equal(6, second.Length);
        Assert.All(second, c => Assert.True(char.IsAsciiDigit(c)));
        Assert.NotEqual(first, second);   // RNG mã hoá, không phải hằng số
    }

    // ── Chặn đường tắt ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_WithWrongOtp_Returns400_AndPasswordUnchanged()
    {
        var h = new Harness();
        var otp = await h.RequestOtpAsync();
        await h.VerifyAsync(otp);

        // Đã verify hợp lệ, nhưng bước reset gửi mã khác → cửa "cầm đúng OTP" phải chặn.
        var result = await h.ResetAsync(Wrong(otp));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(OldPassword, h.CurrentPassword);
    }

    [Fact]
    public async Task ResetPassword_WithoutVerifyStep_Returns400_AndPasswordUnchanged()
    {
        var h = new Harness();
        var otp = await h.RequestOtpAsync();

        // Đây chính là ca chiếm tài khoản: kẻ tấn công biết email, gọi forgot-password rồi nhảy
        // thẳng tới reset-password. Kể cả khi đoán trúng OTP thì thiếu bước verify vẫn phải chặn.
        var result = await h.ResetAsync(otp);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(OldPassword, h.CurrentPassword);
    }

    [Fact]
    public async Task ResetPassword_WithNoOtpAtAll_Returns400_AndPasswordUnchanged()
    {
        var h = new Harness();

        // Không hề có lượt forgot-password nào — không được 500, và tuyệt đối không được đổi mật khẩu.
        var result = await h.ResetAsync("123456");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(OldPassword, h.CurrentPassword);
    }

    [Fact]
    public async Task VerifyOtp_AfterExpiry_Returns400_AndResetStaysBlocked()
    {
        var h = new Harness();
        var otp = await h.RequestOtpAsync();
        h.ExpireOtp();

        Assert.IsType<BadRequestObjectResult>(await h.VerifyAsync(otp));
        Assert.IsType<BadRequestObjectResult>(await h.ResetAsync(otp));
        Assert.Equal(OldPassword, h.CurrentPassword);
    }

    [Fact]
    public async Task ResetPassword_AfterVerifiedWindowLapsed_Returns400()
    {
        var h = new Harness();
        var otp = await h.RequestOtpAsync();
        await h.VerifyAsync(otp);

        // Verify từ lâu rồi mới quay lại đổi mật khẩu → cờ đã-verify hết hiệu lực.
        h.AgeVerifiedFlag(TimeSpan.FromMinutes(30));

        Assert.IsType<BadRequestObjectResult>(await h.ResetAsync(otp));
        Assert.Equal(OldPassword, h.CurrentPassword);
    }

    [Fact]
    public async Task ResetPassword_SameOtpTwice_SecondAttemptFails()
    {
        var h = new Harness();
        var otp = await h.RequestOtpAsync();
        await h.VerifyAsync(otp);
        Assert.IsType<OkObjectResult>(await h.ResetAsync(otp));

        // OTP dùng-một-lần: sau khi đổi mật khẩu thành công thì mã + cờ verify phải bị xoá sạch,
        // nếu không ai đọc được mã cũ (mail, log) vẫn đổi lại mật khẩu lần nữa.
        h.CurrentPassword = NewPassword;
        var second = await h.ResetAsync(otp, "EvenNewerPassw0rd!");

        Assert.IsType<BadRequestObjectResult>(second);
        Assert.Equal(NewPassword, h.CurrentPassword);
        Assert.Null(h.GetToken("OTPCode"));
        Assert.Null(h.GetToken("OtpVerified"));
    }

    [Fact]
    public async Task VerifyOtp_TooManyWrongGuesses_BurnsTheOtp()
    {
        var h = new Harness();
        var otp = await h.RequestOtpAsync();

        // OTP chỉ có 10^6 khả năng → không chặn số lần đoán thì dò hết trong vài phút.
        for (var i = 0; i < 6; i++)
            Assert.IsType<BadRequestObjectResult>(await h.VerifyAsync(Wrong(otp)));

        // Kể cả mã ĐÚNG cũng không còn dùng được: phải xin mã mới.
        Assert.IsType<BadRequestObjectResult>(await h.VerifyAsync(otp));
        Assert.Null(h.GetToken("OTPCode"));
        Assert.Equal(OldPassword, h.CurrentPassword);
    }

    private static string Wrong(string otp) => otp == "000000" ? "111111" : "000000";

    // ── Harness ────────────────────────────────────────────────────────────────

    /// <summary>
    /// AuthController thật + UserManager mock có kho token/mật khẩu trong bộ nhớ.
    /// <see cref="RequestOtpAsync"/> chạy forgot-password thật rồi đọc OTP ra từ kho —
    /// test không tự bịa mã nên luôn khớp với thứ endpoint thực sự sinh ra.
    /// </summary>
    private sealed class Harness
    {
        private readonly Dictionary<string, string> _tokens = new();
        private readonly User _user = new()
        {
            Id = Guid.NewGuid(),
            UserName = Email,
            Email = Email,
            FullName = "Reset Tester",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        public string CurrentPassword { get; set; } = OldPassword;
        public AuthController Controller { get; }

        public Harness()
        {
            var mgr = new Mock<UserManager<User>>(
                Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);

            mgr.Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
                .Returns<string>(e => Task.FromResult(
                    string.Equals(e, Email, StringComparison.OrdinalIgnoreCase) ? _user : null));

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

            var signIn = new Mock<SignInManager<User>>(mgr.Object, Mock.Of<IHttpContextAccessor>(),
                Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);

            Controller = new AuthController(
                Mock.Of<IAuthService>(),
                mgr.Object,
                signIn.Object,
                Mock.Of<IEmailSender>(),
                Mock.Of<IGoogleLoginRedirects>(),
                Mock.Of<IGoogleAuthCodeStore>(),
                Mock.Of<IGoogleIdTokenVerifier>(),
                Mock.Of<ILogger<AuthController>>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public async Task<string> RequestOtpAsync()
        {
            await Controller.ForgotPassword(new ForgotPasswordDto { Email = Email });
            return _tokens["OTPCode"];
        }

        public Task<IActionResult> VerifyAsync(string otp) =>
            Controller.VerifyOtp(new VerifyOtpDto { Email = Email, Otp = otp });

        public Task<IActionResult> ResetAsync(string otp, string newPassword = NewPassword) =>
            Controller.ResetPassword(new ResetPasswordDto
            {
                Email = Email,
                Otp = otp,
                NewPassword = newPassword
            });

        public string? GetToken(string name) => _tokens.TryGetValue(name, out var v) ? v : null;

        public void ExpireOtp() =>
            _tokens["OTPExpiry"] = DateTime.UtcNow.AddMinutes(-1).ToString("O");

        public void AgeVerifiedFlag(TimeSpan age) =>
            _tokens["OtpVerified"] = DateTime.UtcNow.Subtract(age).ToString("O");
    }
}
