using Isas.CampaignService.Services;

namespace Isas.CampaignService.Tests;

public class CampaignEmailSenderTemplateTests
{
    [Fact]
    public void HtmlTemplate_HienThiThongTinMoiVaEncodeDuLieuDong()
    {
        var expiresAt = new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc);
        var html = CampaignEmailSender.BuildHtmlBody(
            "Backend <Senior>",
            "https://fe.test/invite/token?source=email&x=1",
            expiresAt);

        Assert.Contains("Bạn được mời phỏng vấn cùng AI", html);
        Assert.Contains("Tham gia phỏng vấn AI", html);
        Assert.Contains("padding:32px;color:#172033", html);
        Assert.Contains("color:#172033;font-size:16px", html);
        Assert.Contains("Backend &lt;Senior&gt;", html);
        Assert.Contains("https://fe.test/invite/token?source=email&amp;x=1", html);
        Assert.Contains("2026-08-15 09:30 UTC", html);
        Assert.Contains("không chuyển tiếp email hoặc liên kết này", html);
    }

    [Fact]
    public void PlainTextTemplate_GiuLinkVaBoQuaHanKhiKhongCo()
    {
        var body = CampaignEmailSender.BuildPlainTextBody(
            "Backend Q3", "https://fe.test/invite/token", null);

        Assert.Contains("Backend Q3", body);
        Assert.Contains("https://fe.test/invite/token", body);
        Assert.DoesNotContain("Thời hạn tham gia", body);
    }
}
