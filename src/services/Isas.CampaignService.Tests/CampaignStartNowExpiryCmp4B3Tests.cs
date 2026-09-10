using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP4-B3 — chỉ còn MỘT đường kéo giờ mở campaign, và nó luôn để lại vết.
///
/// <list type="bullet">
///   <item><b>start-now</b> nay kiểm <c>ExpiresAt</c>: campaign đã hết hạn ⇒ <b>409</b> TRƯỚC khi ghi
///     gì (không audit, không outbox). Trước CMP4-B3 nó chỉ kiểm Status / ca thi / StartsAt — campaign
///     hết hạn vẫn 200, ghi audit, gửi thư "vào thi ngay" cho toàn bộ ứng viên mà mọi lời mời đã chết
///     (hạn lời mời lấy từ chính <c>campaign.ExpiresAt</c> — <c>ResolveInvitationExpiry</c>).</item>
///   <item>Thư "mở sớm" nay chỉ gửi cho lời mời CHƯA revoke <b>VÀ</b> chưa hết hạn.</item>
///   <item><c>PUT /campaign</c> KHÔNG còn đổi được <c>StartsAt</c> khi campaign Active — bắt đi qua
///     <c>POST /campaign/{id}/start-now</c> (cửa sau của CMP3-B4 nay đã đóng). Draft thì sửa bình thường.</item>
/// </list>
/// </summary>
public class CampaignStartNowExpiryCmp4B3Tests
{
    private static IEntitlementClient Entitlements()
    {
        var client = new Mock<IEntitlementClient>();
        client.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignEntitlement("test", "business", 1, 10, 200, true, true, true));
        return client.Object;
    }

    private static CampaignSvc NewSvc(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            entitlements: Entitlements());

    private static readonly DateTime FarFuture = new(2099, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static Guid Seed(
        CampaignTestDb tdb, Guid owner,
        CampaignStatus status = CampaignStatus.Active,
        DateTime? startsAt = null,
        DateTime? expiresAt = null)
    {
        var camp = CampaignTestDb.NewCampaign(owner, status);
        camp.StartsAt = startsAt ?? FarFuture;
        camp.ExpiresAt = expiresAt;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp.Id;
    }

    private static void AddInvitation(CampaignTestDb tdb, Guid campId, string email,
        DateTime? expiresAt = null, DateTime? revokedAt = null)
        => tdb.Db.CampaignInvitations.Add(new CampaignInvitation
        {
            Id = Guid.NewGuid(), CampaignId = campId, TokenHash = Guid.NewGuid().ToString(),
            Email = email,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(1),
            CreatedAt = DateTime.UtcNow, RevokedAt = revokedAt,
        });

    // ── (1) ExpiresAt quá khứ ⇒ start-now 409, KHÔNG audit, KHÔNG outbox, StartsAt không đổi ──────
    [Fact]
    public async Task StartEarly_ExpiresAt_qua_khu_thi_409_khong_audit_khong_outbox()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, expiresAt: DateTime.UtcNow.AddHours(-1));
        AddInvitation(tdb, campId, "a@x.com");
        tdb.Db.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default));
        Assert.Contains("hết hạn", ex.Message);

        using var check = tdb.NewContext();
        Assert.Equal(FarFuture, (await check.Campaigns.AsNoTracking().SingleAsync(c => c.Id == campId)).StartsAt);
        Assert.Empty(await check.AuditLogs.Where(a => a.EntityId == campId).ToListAsync());
        Assert.Empty(await check.OutboxMessages.Where(m => m.CampaignId == campId).ToListAsync());
    }

    // ── (2) Campaign còn hạn nhưng có lời mời đã hết hạn ⇒ lời mời đó KHÔNG nhận thư "mở sớm" ──────
    [Fact]
    public async Task StartEarly_loi_moi_da_het_han_khong_nhan_thu_mo_som()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, expiresAt: DateTime.UtcNow.AddDays(1));   // campaign còn hạn
        AddInvitation(tdb, campId, "live@x.com", expiresAt: DateTime.UtcNow.AddDays(1));
        AddInvitation(tdb, campId, "expired@x.com", expiresAt: DateTime.UtcNow.AddHours(-1));
        AddInvitation(tdb, campId, "revoked@x.com", revokedAt: DateTime.UtcNow);
        tdb.Db.SaveChanges();

        await NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default);

        using var check = tdb.NewContext();
        var rows = await check.OutboxMessages.Where(m => m.CampaignId == campId).ToListAsync();
        var row = Assert.Single(rows);
        var job = System.Text.Json.JsonSerializer.Deserialize<InvitationEmailJob>(row.Payload,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("live@x.com", job.Email);
    }

    // ── (3) PUT /campaign đổi startsAt khi Active ⇒ 409, StartsAt không đổi ───────────────────────
    [Fact]
    public async Task PUT_campaign_doi_startsAt_khi_Active_thi_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Active);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
                new UpdateCampaignRequest { StartsAt = DateTime.UtcNow }, default));
        Assert.Contains("start-now", ex.Message);

        Assert.Equal(FarFuture, tdb.NewContext().Campaigns.Single(c => c.Id == campId).StartsAt);
    }

    // ── (4) PUT /campaign đổi startsAt khi Draft ⇒ 200, được lưu ─────────────────────────────────
    [Fact]
    public async Task PUT_campaign_doi_startsAt_khi_Draft_thi_200()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Draft);
        var newStart = new DateTime(2099, 5, 5, 8, 0, 0, DateTimeKind.Utc);

        var res = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
            new UpdateCampaignRequest { StartsAt = newStart }, default);

        Assert.Equal(newStart, res.StartsAt);
        Assert.Equal(newStart, tdb.NewContext().Campaigns.Single(c => c.Id == campId).StartsAt);
    }

    // ── (5) start-now hợp lệ vẫn 200 + đúng 1 audit StartEarly (test cũ CMP3-B4 không được đỏ) ────
    [Fact]
    public async Task StartEarly_hop_le_van_200_va_dung_1_audit_StartEarly()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, expiresAt: FarFuture);   // campaign còn hạn xa

        var t0 = DateTime.UtcNow;
        var res = await NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default);
        var t1 = DateTime.UtcNow;

        Assert.NotNull(res.StartsAt);
        Assert.InRange(res.StartsAt!.Value, t0.AddSeconds(-1), t1.AddSeconds(1));

        using var check = tdb.NewContext();
        var audits = await check.AuditLogs
            .Where(a => a.EntityId == campId && a.Action == AuditAction.StartEarly).ToListAsync();
        Assert.Single(audits);
    }

    // ── (6) PUT /campaign đổi expiresAt khi Active ⇒ vẫn 200 (gia hạn là hợp lệ, KHÔNG đụng CMP4-B3) ─
    [Fact]
    public async Task PUT_campaign_doi_expiresAt_khi_Active_van_200()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, CampaignStatus.Active, expiresAt: DateTime.UtcNow.AddDays(1));
        var newExp = new DateTime(2099, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        var res = await NewSvc(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId,
            new UpdateCampaignRequest { ExpiresAt = newExp }, default);

        Assert.Equal(newExp, res.ExpiresAt);
    }

    // ── (7) ExpiresAt null ⇒ guard CMP4-B3 KHÔNG kích hoạt (regression cho CMP3-B4 seed null) ─────
    [Fact]
    public async Task StartEarly_ExpiresAt_null_thi_khong_chan()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, expiresAt: null);

        var res = await NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default);

        Assert.NotNull(res.StartsAt);
        Assert.NotEqual(FarFuture, res.StartsAt);   // đã kéo về now
    }
}
