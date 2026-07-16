using Isas.PaymentService.Models;
using Isas.PaymentService.Services;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Redirect PayOS theo khu vực người mua: URL do FE truyền (candidate/employer) thắng config chung;
/// URL rác/không tuyệt đối → bỏ qua, fallback config; rỗng cả 2 → PaymentGatewayException (BF3).
/// </summary>
public class PayosUrlResolverTests
{
    private static PayOSSettings Cfg(string ret, string cancel) => new()
    {
        ClientId = "x", ApiKey = "x", ChecksumKey = "x", ReturnUrl = ret, CancelUrl = cancel,
    };

    [Fact]
    public void RequestUrls_Override_Config()
    {
        var (ret, cancel) = PayosUrlResolver.Resolve(
            "https://fe.app/employer/payment/success",
            "https://fe.app/employer/payment/cancel",
            Cfg("https://fe.app/candidate/payment/success", "https://fe.app/candidate/payment/cancel"));

        Assert.Equal("https://fe.app/employer/payment/success", ret);
        Assert.Equal("https://fe.app/employer/payment/cancel", cancel);
    }

    [Fact]
    public void MissingRequestUrls_FallBackToConfig()
    {
        var (ret, cancel) = PayosUrlResolver.Resolve(null, null,
            Cfg("https://fe.app/candidate/payment/success", "https://fe.app/candidate/payment/cancel"));

        Assert.Equal("https://fe.app/candidate/payment/success", ret);
        Assert.Equal("https://fe.app/candidate/payment/cancel", cancel);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/employer/payment/success")]   // tương đối — không nhận
    [InlineData("javascript:alert(1)")]          // chống open-redirect/scheme lạ
    [InlineData("ftp://x/y")]
    public void InvalidRequestUrl_FallsBackToConfig(string badUrl)
    {
        var (ret, _) = PayosUrlResolver.Resolve(badUrl, badUrl,
            Cfg("https://fe.app/candidate/payment/success", "https://fe.app/candidate/payment/cancel"));

        Assert.Equal("https://fe.app/candidate/payment/success", ret);   // dùng config, KHÔNG dùng URL rác
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void BothSourcesEmpty_Throws(string? ret, string? cancel)
    {
        Assert.Throws<PaymentGatewayException>(() =>
            PayosUrlResolver.Resolve(null, null, Cfg(ret!, cancel!)));
    }

    [Fact]
    public void ValidRequest_RescuesEmptyConfig()
    {
        // Config rỗng nhưng FE truyền URL hợp lệ → KHÔNG throw (redirect vẫn có đích).
        var (ret, cancel) = PayosUrlResolver.Resolve(
            "https://fe.app/candidate/payment/success",
            "https://fe.app/candidate/payment/cancel",
            Cfg("", ""));

        Assert.Equal("https://fe.app/candidate/payment/success", ret);
        Assert.Equal("https://fe.app/candidate/payment/cancel", cancel);
    }
}
