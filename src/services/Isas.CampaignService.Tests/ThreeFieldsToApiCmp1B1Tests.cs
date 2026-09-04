using System.Net;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP1-B1 — ba trường sự thật ra API: <c>jdFileUrl</c> (CampaignResponse) · <c>startsAt</c> +
/// <c>orgName</c> (trang lời mời). Thuần additive, 0 migration — dữ liệu đã có, chỉ chưa lộ ra.
///
/// <para>Đã đo trên dev: <c>CampaignResponse</c> chưa bao giờ khai <c>JdFileUrl</c> ⇒ FE báo "Tải lên
/// thành công" cho tệp JD vừa bị luật C11 vứt. Trang lời mời trả đúng 6 trường, không có
/// <c>startsAt</c>, và <c>orgName</c> luôn null.</para>
/// </summary>
public class ThreeFieldsToApiCmp1B1Tests
{
    // ── helpers CampaignService (nhái CampaignTextInputTests) ──────────────────────
    private static CampaignSvc NewCampaignService(CampaignDbContext db, IParserService? parser = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            parser ?? Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static IFormFile MakePdf(string name = "jd.pdf")
    {
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 fake");
        var mock = new Mock<IFormFile>();
        mock.Setup(f => f.ContentType).Returns("application/pdf");
        mock.Setup(f => f.Length).Returns(bytes.Length);
        mock.Setup(f => f.FileName).Returns(name);
        mock.Setup(f => f.Name).Returns("file");
        mock.Setup(f => f.Headers).Returns(Mock.Of<IHeaderDictionary>());
        mock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(bytes));
        return mock.Object;
    }

    private static Mock<IParserService> ParserReturning(string text)
    {
        var p = new Mock<IParserService>();
        p.Setup(x => x.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
         .ReturnsAsync(new ParseResult { RawText = text });
        return p;
    }

    // ── helpers ParticipationService ─────────────────────────────────────────────
    private static ParticipationService NewParticipation(
        CampaignDbContext db, Mock<IOrgNameResolver>? resolver = null)
    {
        var auth = new Mock<IAuthProvisionClient>();
        var session = new Mock<ICampaignSessionClient>();
        return new ParticipationService(
            db, auth.Object, session.Object,
            NullLogger<ParticipationService>.Instance,
            resolver?.Object);
    }

    private static string RawTokenOf(CampaignInvitation inv) => inv.Id.ToString("N");

    private static CampaignInvitation NewInvitation(Guid campaignId, string email = "cand@acme.test")
    {
        var id = Guid.NewGuid();
        return new()
        {
            Id = id,
            CampaignId = campaignId,
            TokenHash = InvitationTokens.Hash(id.ToString("N")),
            Email = email,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 1. Upload JD khi jdText rỗng ⇒ jdFileUrl có giá trị (khoá S3).
    [Fact]
    public async Task Upload_JD_khi_jdText_rong_thi_jdFileUrl_co_gia_tri()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        // jdText rỗng + JDFileUrl rỗng ⇒ HasDirectText false ⇒ file được nhận.
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewCampaignService(tdb.NewContext(), ParserReturning("JD từ PDF").Object);
        var res = await svc.UploadCampaignFilesAsync(owner, camp.Id,
            new UploadCampaignFilesRequest { JdFile = MakePdf() }, default);

        Assert.Equal($"campaigns/{camp.Id}/jd.pdf", res.JdFileUrl);

        using var check = tdb.NewContext();
        var row = await check.Campaigns.FirstAsync(c => c.Id == camp.Id);
        Assert.Equal(res.JdFileUrl, row.JDFileUrl);   // API nói đúng cái DB lưu
    }

    // 2. Upload khi đã có jdText ⇒ jdFileUrl vẫn null (luật C11 "text ưu tiên file" GIỮ NGUYÊN).
    [Fact]
    public async Task Upload_JD_khi_da_co_jdText_thi_jdFileUrl_van_null()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        camp.JDText = "JD nhập trực tiếp";   // HasDirectText true ⇒ file bị bỏ
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var parser = ParserReturning("FROM-PDF");
        var svc = NewCampaignService(tdb.NewContext(), parser.Object);
        var res = await svc.UploadCampaignFilesAsync(owner, camp.Id,
            new UploadCampaignFilesRequest { JdFile = MakePdf() }, default);

        Assert.Null(res.JdFileUrl);
        Assert.Equal("JD nhập trực tiếp", res.JDText);   // text không bị PDF ghi đè
        parser.Verify(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // 3. GET /campaign/{id} có khoá jdFileUrl với đúng giá trị đã lưu.
    [Fact]
    public async Task GetCampaign_response_co_khoa_jdFileUrl()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.JDFileUrl = "campaigns/abc/jd.pdf";
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewCampaignService(tdb.NewContext());
        var res = await svc.GetCampaignAsync(owner, camp.Id, default);

        Assert.Equal("campaigns/abc/jd.pdf", res.JdFileUrl);

        var json = JsonSerializer.Serialize(res, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"jdFileUrl\":\"campaigns/abc/jd.pdf\"", json);
    }

    // 4. Metadata lời mời có startsAt = campaign.StartsAt; deadline VẪN là campaign.ExpiresAt (không đổi nghĩa).
    [Fact]
    public async Task Metadata_co_startsAt_dung_gia_tri_campaign_va_deadline_khong_doi_nghia()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.StartsAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        camp.ExpiresAt = new DateTime(2026, 6, 20, 17, 0, 0, DateTimeKind.Utc);
        var inv = NewInvitation(camp.Id);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var meta = await NewParticipation(tdb.NewContext()).GetInvitationMetadataAsync(RawTokenOf(inv), default);

        Assert.Equal(camp.StartsAt, meta.StartsAt);
        Assert.Equal(camp.ExpiresAt, meta.Deadline);   // deadline = hạn LỜI MỜI, KHÔNG phải startsAt
        Assert.NotEqual(meta.StartsAt, meta.Deadline);
    }

    // 5. Resolver NÉM ⇒ orgName = null và metadata VẪN trả về 200 (ứng viên ẩn danh phải mở được).
    [Fact]
    public async Task Metadata_resolver_nem_thi_orgName_null_va_van_tra_ve()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var inv = NewInvitation(camp.Id);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var resolver = new Mock<IOrgNameResolver>();
        resolver.Setup(x => x.ResolveOrgNameAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Auth down"));

        var meta = await NewParticipation(tdb.NewContext(), resolver)
            .GetInvitationMetadataAsync(RawTokenOf(inv), default);

        Assert.Null(meta.OrgName);
        Assert.Equal(camp.Id, meta.CampaignId);   // metadata vẫn nguyên vẹn
    }

    // 6. Resolver trả null ⇒ orgName null (hôm nay: Auth chưa có tên → null).
    [Fact]
    public async Task Metadata_resolver_tra_null_thi_orgName_null()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var inv = NewInvitation(camp.Id);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var resolver = new Mock<IOrgNameResolver>();
        resolver.Setup(x => x.ResolveOrgNameAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

        var meta = await NewParticipation(tdb.NewContext(), resolver)
            .GetInvitationMetadataAsync(RawTokenOf(inv), default);

        Assert.Null(meta.OrgName);
    }

    // 7. Resolver trả tên ⇒ orgName = tên đó, và resolver được hỏi ĐÚNG org_id của campaign.
    [Fact]
    public async Task Metadata_orgName_resolve_thanh_cong()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        var inv = NewInvitation(camp.Id);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var resolver = new Mock<IOrgNameResolver>();
        resolver.Setup(x => x.ResolveOrgNameAsync(orgId, It.IsAny<CancellationToken>()))
                .ReturnsAsync("Công ty Acme");

        var meta = await NewParticipation(tdb.NewContext(), resolver)
            .GetInvitationMetadataAsync(RawTokenOf(inv), default);

        Assert.Equal("Công ty Acme", meta.OrgName);
        resolver.Verify(x => x.ResolveOrgNameAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // 8. HỢP ĐỒNG JSON — đúng 3 tên khoá camelCase.
    [Fact]
    public void Contract_JSON_keys_camelCase()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var campJson = JsonSerializer.Serialize(
            new CampaignResponse { JdFileUrl = "campaigns/x/jd.pdf" }, web);
        Assert.Contains("\"jdFileUrl\"", campJson);
        Assert.DoesNotContain("\"JdFileUrl\"", campJson);
        Assert.DoesNotContain("\"jDFileUrl\"", campJson);

        var metaJson = JsonSerializer.Serialize(
            new InvitationMetadataResponse
            {
                StartsAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
                OrgName = "Acme"
            }, web);
        Assert.Contains("\"startsAt\"", metaJson);
        Assert.Contains("\"orgName\"", metaJson);
    }

    // 9. Resolver THẬT (AuthOrgNameResolver): non-2xx / lỗi ⇒ null, KHÔNG ném; 200 ⇒ tên đã trim.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? Last { get; private set; }
        public StubHandler(HttpStatusCode status, string body = "") { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static AuthOrgNameResolver NewResolver(StubHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tok" })
            .Build();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://auth.test") };
        return new AuthOrgNameResolver(http, config, NullLogger<AuthOrgNameResolver>.Instance);
    }

    [Fact]
    public async Task AuthOrgNameResolver_non2xx_tra_null_khong_nem()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError);
        var name = await NewResolver(handler).ResolveOrgNameAsync(Guid.NewGuid(), default);

        Assert.Null(name);
        Assert.True(handler.Last!.Headers.TryGetValues("X-Internal-Token", out var v));
        Assert.Equal("tok", Assert.Single(v!));
    }

    [Fact]
    public async Task AuthOrgNameResolver_200_tra_ten_da_trim()
    {
        var orgId = Guid.NewGuid();
        var handler = new StubHandler(HttpStatusCode.OK, $$"""{"id":"{{orgId}}","name":"  Công ty Acme  "}""");
        var name = await NewResolver(handler).ResolveOrgNameAsync(orgId, default);

        Assert.Equal("Công ty Acme", name);
        Assert.Equal($"/internal/auth/organizations/{orgId}", handler.Last!.RequestUri!.AbsolutePath);
    }
}
