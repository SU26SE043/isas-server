using System.Net;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// INT-17 — toggle phỏng vấn THÍCH ỨNG theo TỪNG CHIẾN DỊCH (HR bật).
///
/// Trước đây CampaignService KHÔNG gửi cờ nào xuống Interview ⇒ B2B adaptive không bao giờ bật được
/// (E2E 2026-07-18 phải ép cờ bằng SQL mới test được đuôi thích ứng). Bộ test khoá 3 mắt xích:
///   (1) create/update round-trip (bool? = null → giữ giá trị cũ, như AntiCheatEnabled/C3)
///   (2) Start truyền cờ campaign xuống ICampaignSessionClient
///   (3) cờ thật sự nằm trong payload JSON gửi Interview /internal/sessions/campaign
/// Helper để local (các test file khác khai private) → file này tự chứa.
/// </summary>
public class CampaignAdaptiveToggleTests
{
    private static CampaignSvc NewCampaignService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static CreateCampaignRequest BaseCreate(string title = "Campaign") => new()
    {
        Title = title,
        Domain = "BE",
        TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddMinutes(5),
        ExpiresAt = DateTime.UtcNow.AddDays(2),
    };

    // ── (1) Create / Update round-trip ──────────────────────────────────
    [Fact]
    public async Task Create_BatAdaptive_LuuVaTraVe()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var req = BaseCreate("Adaptive campaign");
        req.AdaptiveEnabled = true;
        req.MaxFollowUps = 2;
        req.MaxQuestions = 8;

        var res = await NewCampaignService(tdb.NewContext()).CreateCampaignAsync(org, org, req, default);

        Assert.True(res.AdaptiveEnabled);
        Assert.Equal(2, res.MaxFollowUps);
        Assert.Equal(8, res.MaxQuestions);

        var saved = await tdb.NewContext().Campaigns.AsNoTracking().SingleAsync(c => c.Id == res.Id);
        Assert.True(saved.AdaptiveEnabled);
        Assert.Equal(2, saved.MaxFollowUps);
        Assert.Equal(8, saved.MaxQuestions);
    }

    [Fact]
    public async Task Create_MacDinh_AdaptiveTat()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var res = await NewCampaignService(tdb.NewContext())
            .CreateCampaignAsync(org, org, BaseCreate("Static"), default);

        Assert.False(res.AdaptiveEnabled);   // không gửi → tắt = luồng batch tĩnh cũ
        Assert.Null(res.MaxFollowUps);
        Assert.Null(res.MaxQuestions);
    }

    [Fact]
    public async Task Update_AdaptiveNull_GiuGiaTriCu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        camp.AdaptiveEnabled = true;
        camp.MaxFollowUps = 3;
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var res = await NewCampaignService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "Đổi tên", AdaptiveEnabled = null }, default);

        Assert.True(res.AdaptiveEnabled);    // null = KHÔNG đổi (mẫu C3 AntiCheatEnabled)
        Assert.Equal(3, res.MaxFollowUps);
    }

    [Fact]
    public async Task Update_TatAdaptive_GhiDe()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        camp.AdaptiveEnabled = true;
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var res = await NewCampaignService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Title = "x", AdaptiveEnabled = false }, default);

        Assert.False(res.AdaptiveEnabled);
    }

    [Fact]
    public async Task Create_TranAm_BiChan()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var req = BaseCreate();
        req.AdaptiveEnabled = true;
        req.MaxFollowUps = -1;   // âm → chặn ở code (khớp CHECK ck_campaigns_adaptive_caps_non_negative)

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewCampaignService(tdb.NewContext()).CreateCampaignAsync(org, org, req, default));
    }

    // ── (3) Payload JSON gửi Interview ──────────────────────────────────
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }
        private readonly Guid _sessionId;
        public CapturingHandler(Guid sessionId) => _sessionId = sessionId;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"id\":\"{_sessionId}\",\"questions\":[]}}",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private static CampaignSessionClient NewSessionClient(CapturingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://interview.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return new CampaignSessionClient(http, config, NullLogger<CampaignSessionClient>.Instance);
    }

    private static readonly IReadOnlyList<string> Qs = new List<string> { "Q1" };
    private static readonly IReadOnlyList<SessionCriterionInput> Crits =
        new List<SessionCriterionInput> { new("Communication", null, 1.0m, 5) };

    [Fact]
    public async Task Payload_ChuaCoAdaptive_KhiHrBat()
    {
        var handler = new CapturingHandler(Guid.NewGuid());

        await NewSessionClient(handler).CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE", Qs, Crits,
            expiresAt: null, adaptiveEnabled: true, maxFollowUps: 2, maxQuestions: 8, ct: default);

        Assert.NotNull(handler.CapturedBody);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.True(doc.RootElement.GetProperty("adaptiveEnabled").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("maxFollowUps").GetInt32());
        Assert.Equal(8, doc.RootElement.GetProperty("maxQuestions").GetInt32());
    }

    [Fact]
    public async Task Payload_AdaptiveNull_KhiCampaignKhongBat()
    {
        var handler = new CapturingHandler(Guid.NewGuid());

        await NewSessionClient(handler).CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE", Qs, Crits, ct: default);

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        // null → Interview đọc `?? false` ⇒ giữ luồng tĩnh (không bịa true).
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("adaptiveEnabled").ValueKind);
    }
}
