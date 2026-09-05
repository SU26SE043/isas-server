using System.Text.Json;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP1-B4 — thư mời nói đủ 3 điều mới: giờ campaign MỞ, tên công ty mời, có cần camera/mic không.
///
/// <para>Đã đo trên dev: thư chỉ chèn 5 biến (tên chiến dịch, link, hạn, giờ slot đầu/cuối), chữ ký
/// LUÔN là "Đội ngũ ISAS" dù bên mời là công ty khác, và campaign bật cả antiCheatEnabled lẫn
/// faceVerifyEnabled vẫn không hề nói ứng viên sẽ bị bật camera.</para>
/// </summary>
public class CampaignInvitationEmailCmp1B4Tests
{
    private static readonly string Link = "https://fe.test/invite/tok";
    private static readonly string Title = "Backend Q3";

    // ── 1-3. Dòng "Phỏng vấn mở từ" — chỉ khi startsAt còn ở TƯƠNG LAI ─────────────────────────

    [Fact]
    public void StartsAt_TuongLai_CoDongGioMo_CaHaiBan()
    {
        var startsAt = DateTime.UtcNow.AddDays(3);

        var html = CampaignEmailSender.BuildHtmlBody(Title, Link, null, null, null, startsAt);
        var plain = CampaignEmailSender.BuildPlainTextBody(Title, Link, null, null, null, startsAt);

        Assert.Contains("Phỏng vấn mở từ", html);
        Assert.Contains("Phỏng vấn mở từ", plain);
        Assert.Contains("(giờ VN)", html);
    }

    [Fact]
    public void StartsAt_QuaKhu_KHONG_CoDongGioMo()
    {
        var startsAt = DateTime.UtcNow.AddDays(-1);   // campaign đã mở rồi

        var html = CampaignEmailSender.BuildHtmlBody(Title, Link, null, null, null, startsAt);
        var plain = CampaignEmailSender.BuildPlainTextBody(Title, Link, null, null, null, startsAt);

        Assert.DoesNotContain("Phỏng vấn mở từ", html);
        Assert.DoesNotContain("Phỏng vấn mở từ", plain);
    }

    [Fact]
    public void StartsAt_Null_KHONG_CoDongGioMo()
    {
        var html = CampaignEmailSender.BuildHtmlBody(Title, Link, null, null, null, startsAt: null);
        var plain = CampaignEmailSender.BuildPlainTextBody(Title, Link, null, null, null, startsAt: null);

        Assert.DoesNotContain("Phỏng vấn mở từ", html);
        Assert.DoesNotContain("Phỏng vấn mở từ", plain);
    }

    // ── 4-5. Dòng chuẩn bị (thời lượng + camera/mic) ─────────────────────────────────────────

    [Fact]
    public void FaceVerifyEnabled_False_KHONG_CoDongCamera_NhungVanCoThoiLuong()
    {
        var html = CampaignEmailSender.BuildHtmlBody(
            Title, Link, null, null, null, null, orgName: null, faceVerifyEnabled: false, timeLimitMinutes: 45);
        var plain = CampaignEmailSender.BuildPlainTextBody(
            Title, Link, null, null, null, null, orgName: null, faceVerifyEnabled: false, timeLimitMinutes: 45);

        Assert.DoesNotContain("camera", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("camera", plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("45 phút", html);
        Assert.Contains("45 phút", plain);
    }

    [Fact]
    public void FaceVerifyEnabled_True_CoDongCamera_KemThoiLuong()
    {
        var html = CampaignEmailSender.BuildHtmlBody(
            Title, Link, null, null, null, null, orgName: null, faceVerifyEnabled: true, timeLimitMinutes: 45);
        var plain = CampaignEmailSender.BuildPlainTextBody(
            Title, Link, null, null, null, null, orgName: null, faceVerifyEnabled: true, timeLimitMinutes: 45);

        Assert.Contains("cần camera và micro", html);
        Assert.Contains("cần camera và micro", plain);
        Assert.Contains("45 phút · cần camera và micro", html);
        Assert.Contains("45 phút · cần camera và micro", plain);
    }

    [Fact]
    public void FaceVerifyEnabled_True_KhongCoThoiLuong_ChiHienCamera()
    {
        // timeLimitMinutes null ⇒ phần "X phút" không xuất hiện, nhưng dòng camera vẫn phải hiện —
        // hai mảnh của BuildPrepLine độc lập với nhau, không phụ thuộc lẫn nhau.
        var html = CampaignEmailSender.BuildHtmlBody(
            Title, Link, null, null, null, null, orgName: null, faceVerifyEnabled: true, timeLimitMinutes: null);

        Assert.Contains("cần camera và micro", html);
        Assert.DoesNotContain("phút", html);
    }

    [Fact]
    public void KhongFaceVerify_KhongThoiLuong_KhongCoDongChuanBi()
    {
        var html = CampaignEmailSender.BuildHtmlBody(
            Title, Link, null, null, null, null, orgName: null, faceVerifyEnabled: false, timeLimitMinutes: null);

        Assert.DoesNotContain("Chuẩn bị trước khi vào phỏng vấn", html);
    }

    // ── 6-7. Chữ ký ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrgName_Null_ChuKy_KhongVo_VanLaDoiNguISAS()
    {
        var html = CampaignEmailSender.BuildHtmlBody(Title, Link, null, null, null, orgName: null);
        var plain = CampaignEmailSender.BuildPlainTextBody(Title, Link, null, null, null, orgName: null);

        Assert.Contains("Đội ngũ ISAS", html);
        Assert.Contains("Đội ngũ ISAS", plain);
    }

    [Fact]
    public void OrgName_CoGiaTri_ChuKy_HienThiTenCongTy()
    {
        // ASCII (không dấu) để so trực tiếp — riêng dấu < > mới là thứ đang kiểm tra có bị encode
        // hay không; HtmlEncoder.Default còn escape luôn ký tự có dấu (ú, ô, …) thành entity số, nên
        // orgName tiếng Việt được kiểm ở HtmlEncoder.Encode trực tiếp (test dưới), không so chuỗi thô.
        var html = CampaignEmailSender.BuildHtmlBody(Title, Link, null, null, null, orgName: "Acme <Corp>");
        var plain = CampaignEmailSender.BuildPlainTextBody(Title, Link, null, null, null, orgName: "Công ty Acme");

        Assert.Contains("Acme &lt;Corp&gt;", html);   // encode HTML — orgName là dữ liệu HR nhập (XSS)
        Assert.Contains("nền tảng ISAS", html);
        Assert.Contains("Công ty Acme", plain);
        Assert.DoesNotContain("Đội ngũ ISAS", plain);    // chữ ký cũ bị thay, không cộng dồn
    }

    [Fact]
    public void OrgName_TiengViet_DuocEncodeDungCachTrenBanHtml()
    {
        // HtmlEncoder.Default.Encode tiếng Việt ra HTML entity số (đúng hành vi .NET mặc định, cùng
        // cách campaignTitle/joinLink đã được encode từ trước) — trình duyệt/email client render ra
        // đúng chữ, chỉ MÃ NGUỒN không phải chuỗi thô. Test đối chiếu bằng CHÍNH bộ encode đó.
        var expected = System.Text.Encodings.Web.HtmlEncoder.Default.Encode("Công ty Acme");
        var html = CampaignEmailSender.BuildHtmlBody(Title, Link, null, null, null, orgName: "Công ty Acme");

        Assert.Contains(expected, html);
    }

    // ── 8. .eml hiện có vẫn PASS — không đổi call site cũ (5/6-arg) ─────────────────────────────
    // (khoá lại bằng chính CampaignEmailSenderMimeTests đã có sẵn, chạy trong cùng lượt test)

    // ── 9-10. CHẶNG DÂY — CampaignService phải TRUYỀN ĐỦ 4 trường vào outbox payload ────────────
    // Đây là ca nặng nhất theo đúng cảnh báo của task: thiếu 1 trường thì compile vẫn xanh, thư vẫn
    // gửi, chỉ mất đúng chữ đó. Test đọc THẲNG JSON đã ghi vào outbox_messages, không qua sender mock.

    private static CampaignSvc NewCampaignService(
        CampaignDbContext db, IOrgNameResolver? orgNameResolver = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            orgNameResolver: orgNameResolver);

    [Fact]
    public async Task CreateInvitations_OutboxPayload_MangDuBonTruongMoi()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        camp.StartsAt = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);
        camp.FaceVerifyEnabled = true;
        camp.TimeLimitMinutes = 45;
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var resolver = new Mock<IOrgNameResolver>();
        resolver.Setup(x => x.ResolveOrgNameAsync(orgId, It.IsAny<CancellationToken>()))
                .ReturnsAsync("Công ty Acme");

        var svc = NewCampaignService(tdb.NewContext(), resolver.Object);
        await svc.CreateInvitationsAsync(orgId, orgId, camp.Id, new List<string> { "a@x.com" }, default);

        using var check = tdb.NewContext();
        var outbox = await check.OutboxMessages.SingleAsync(m => m.CampaignId == camp.Id);
        var job = JsonSerializer.Deserialize<InvitationEmailJob>(outbox.Payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(job);
        Assert.Equal(camp.StartsAt, job!.StartsAt);
        Assert.Equal("Công ty Acme", job.OrgName);
        Assert.True(job.FaceVerifyEnabled);
        Assert.Equal(45, job.TimeLimitMinutes);

        // resolver chỉ được hỏi 1 LẦN cho cả batch, không 1 lần/invitation (dù batch này có 1 email —
        // ràng buộc thật ở test dưới với batch nhiều email).
        resolver.Verify(x => x.ResolveOrgNameAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateInvitations_NhieuEmail_ResolverChiGoi_MOT_LanChoCaBatch()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var resolver = new Mock<IOrgNameResolver>();
        resolver.Setup(x => x.ResolveOrgNameAsync(orgId, It.IsAny<CancellationToken>())).ReturnsAsync("Acme");

        var svc = NewCampaignService(tdb.NewContext(), resolver.Object);
        await svc.CreateInvitationsAsync(orgId, orgId, camp.Id,
            new List<string> { "a@x.com", "b@x.com", "c@x.com" }, default);

        resolver.Verify(x => x.ResolveOrgNameAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);

        using var check = tdb.NewContext();
        var outbox = await check.OutboxMessages.Where(m => m.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(3, outbox.Count);
        Assert.All(outbox, m =>
        {
            var job = JsonSerializer.Deserialize<InvitationEmailJob>(m.Payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal("Acme", job!.OrgName);
        });
    }

    // ── 11. Resolver NÉM ⇒ lời mời vẫn tạo được (fail-soft không được chặn đường mời).
    [Fact]
    public async Task CreateInvitations_ResolverNem_VanTaoLoiMoiDuoc_OrgNameNull()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var resolver = new Mock<IOrgNameResolver>();
        resolver.Setup(x => x.ResolveOrgNameAsync(orgId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Auth down"));

        var svc = NewCampaignService(tdb.NewContext(), resolver.Object);
        var result = await svc.CreateInvitationsAsync(orgId, orgId, camp.Id, new List<string> { "a@x.com" }, default);

        Assert.Single(result.Created);   // lời mời vẫn được tạo

        using var check = tdb.NewContext();
        var outbox = await check.OutboxMessages.SingleAsync(m => m.CampaignId == camp.Id);
        var job = JsonSerializer.Deserialize<InvitationEmailJob>(outbox.Payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Null(job!.OrgName);
    }
}
