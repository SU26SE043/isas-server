using Isas.AuthService.Controllers;
using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// <c>POST /auth/google/exchange</c> — chặng 2 của đăng nhập Google (đổi mã dùng-một-lần lấy phiên).
/// Test ở tầng controller với kho mã THẬT, để khoá đúng hợp đồng ra ngoài: mã tốt → 200 kèm token;
/// mã sai/đã dùng/hết hạn → 400 và KHÔNG lộ token.
/// </summary>
public class GoogleExchangeEndpointTests
{
    [Fact]
    public void DoiMaHopLe_Tra200KemToken()
    {
        var store = Store();
        var code = store.Issue(new AuthResponse
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ExpiresAt = new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc)
        });

        var result = Controller(store).ExchangeGoogleCode(new GoogleExchangeRequest { Code = code });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal("access-1", auth.AccessToken);
        Assert.Equal("refresh-1", auth.RefreshToken);
    }

    // Mã chết ngay sau lần đổi đầu → mã lọt ra ngoài cũng không đổi lại được thành phiên thứ hai.
    [Fact]
    public void DoiLanThuHai_Tra400()
    {
        var store = Store();
        var code = store.Issue(Auth());
        var ctrl = Controller(store);

        Assert.IsType<OkObjectResult>(ctrl.ExchangeGoogleCode(new GoogleExchangeRequest { Code = code }).Result);

        var again = ctrl.ExchangeGoogleCode(new GoogleExchangeRequest { Code = code });

        Assert.IsType<BadRequestObjectResult>(again.Result);
    }

    [Fact]
    public void MaBia_Tra400()
    {
        var store = Store();
        store.Issue(Auth());                                // kho có mã thật, nhưng không phải mã gửi lên

        var result = Controller(store).ExchangeGoogleCode(
            new GoogleExchangeRequest { Code = "ma-bia-khong-ton-tai" });

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        // Thông điệp lỗi không được là AuthResponse trá hình.
        Assert.IsNotType<AuthResponse>(bad.Value);
    }

    [Fact]
    public void MaRong_Tra400()
    {
        var result = Controller(Store()).ExchangeGoogleCode(new GoogleExchangeRequest { Code = "" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private static GoogleAuthCodeStore Store() =>
        new(new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>()).Build());

    private static AuthResponse Auth() =>
        new() { AccessToken = "a", RefreshToken = "r", ExpiresAt = DateTime.UtcNow.AddMinutes(15) };

    private static AuthController Controller(IGoogleAuthCodeStore store)
    {
        var userManager = new Mock<UserManager<User>>(
            Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        var signInManager = new Mock<SignInManager<User>>(
            userManager.Object, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);

        return new AuthController(
            Mock.Of<IAuthService>(),
            userManager.Object,
            signInManager.Object,
            Mock.Of<IEmailSender>(),
            Mock.Of<IGoogleLoginRedirects>(),
            store,
            NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }
}
