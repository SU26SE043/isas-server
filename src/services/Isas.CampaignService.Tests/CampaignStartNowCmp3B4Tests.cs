using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP3-B4 — <c>POST /campaign/{id}/start-now</c>: kéo <c>start_at</c> về hiện tại để ứng viên vào
/// thi ngay. KHÔNG nhận body. Đường RIÊNG (không dùng PUT /campaign): nhánh không-criteria của
/// PUT không ghi audit, và body React kèm criteria làm <c>rubric_version</c> nhảy oan.
///
/// <para>Bất biến: kéo giờ sớm LUÔN để lại vết (audit <c>StartEarly</c> mang mốc CŨ) · KHÔNG đường
/// nào bump <c>rubric_version</c> · start_at đã ở quá khứ ⇒ no-op · KHÔNG đụng <c>expires_at</c> ·
/// campaign có ca thi ⇒ 409 (nút này không mở cửa cho ai).</para>
/// </summary>
public class CampaignStartNowCmp3B4Tests
{
    private static readonly JsonSerializerOptions JsonCi = new() { PropertyNameCaseInsensitive = true };

    private static CampaignSvc NewSvc(CampaignDbContext db)
        => new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static readonly DateTime FarFuture = new(2099, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private const string FarFutureIso = "2099-01-01T10:00:00Z";

    private static Guid Seed(
        CampaignTestDb tdb, Guid owner,
        CampaignStatus status = CampaignStatus.Active,
        DateTime? startsAt = null,          // null ở đây = KHÔNG set (khác "chưa hẹn"): dùng sentinel dưới
        bool startsAtIsFarFuture = true,
        DateTime? expiresAt = null,
        int rubricVersion = 1,
        bool withCriterion = false)
    {
        var camp = CampaignTestDb.NewCampaign(owner, status);
        camp.StartsAt = startsAt ?? (startsAtIsFarFuture ? FarFuture : DateTime.UtcNow.AddHours(-2));
        camp.ExpiresAt = expiresAt;
        camp.RubricVersion = rubricVersion;
        tdb.Db.Campaigns.Add(camp);
        if (withCriterion)
            tdb.Db.CampaignCriteria.Add(new CampaignCriterion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Kỹ năng",
                Weight = 1m, MaxScore = 5, Source = CriterionSource.HrEdited,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        tdb.Db.SaveChanges();
        return camp.Id;
    }

    // CMP4-B5 — `emailSentAt` mặc định non-null: thư mở sớm CHỈ gửi cho lời mời đã nhận thư mời.
    private static void AddInvitation(CampaignTestDb tdb, Guid campId, string email,
        DateTime? revokedAt = null, bool emailSent = true)
        => tdb.Db.CampaignInvitations.Add(new CampaignInvitation
        {
            Id = Guid.NewGuid(), CampaignId = campId, TokenHash = Guid.NewGuid().ToString(),
            Email = email, ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow,
            RevokedAt = revokedAt,
            EmailSentAt = emailSent ? DateTime.UtcNow.AddMinutes(-10) : null,
        });

    // ── (1) start_at tương lai ⇒ về ~now + ĐÚNG 1 audit StartEarly có MỐC CŨ trong summary ────
    [Fact]
    public async Task StartsAt_tuong_lai_thi_ve_now_va_dung_1_audit_StartEarly_co_moc_cu()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner);

        var t0 = DateTime.UtcNow;
        var res = await NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default);
        var t1 = DateTime.UtcNow;

        Assert.NotNull(res.StartsAt);
        Assert.InRange(res.StartsAt!.Value, t0.AddSeconds(-1), t1.AddSeconds(1));

        using var check = tdb.NewContext();
        var audits = await check.AuditLogs
            .Where(a => a.EntityId == campId && a.Action == AuditAction.StartEarly).ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Contains(FarFutureIso, audit.Summary);        // mốc CŨ
        Assert.Equal(owner, audit.ActorUserId);
        Assert.Equal(owner, audit.OrgId);
    }

    // ── (2) start_at quá khứ ⇒ no-op: updated_at KHÔNG đổi, 0 audit, 0 outbox ──────────────────
    [Fact]
    public async Task StartsAt_qua_khu_thi_noop_updatedAt_khong_doi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, startsAtIsFarFuture: false);
        AddInvitation(tdb, campId, "a@x.com");
        tdb.Db.SaveChanges();

        var before = await tdb.NewContext().Campaigns.AsNoTracking().SingleAsync(c => c.Id == campId);

        var res = await NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default);

        using var check = tdb.NewContext();
        var after = await check.Campaigns.AsNoTracking().SingleAsync(c => c.Id == campId);
        Assert.Equal(before.StartsAt, after.StartsAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Empty(await check.AuditLogs.Where(a => a.EntityId == campId).ToListAsync());
        Assert.Empty(await check.OutboxMessages.Where(m => m.CampaignId == campId).ToListAsync());
    }

    // ── (3) Draft ⇒ 409 ─────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(CampaignStatus.Draft)]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Khong_Active_thi_409(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, status: status);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default));
        Assert.Contains("Active", ex.Message);

        Assert.Equal(FarFuture, (await tdb.NewContext().Campaigns.AsNoTracking().SingleAsync(c => c.Id == campId)).StartsAt);
    }

    // ── (4) Campaign có ca thi (campaign_slots) ⇒ 409 nói thẳng, KHÔNG im lặng 200 ───────────
    [Fact]
    public async Task Co_ca_thi_thi_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner);
        tdb.Db.CampaignSlots.Add(new CampaignSlot
        {
            Id = Guid.NewGuid(), CampaignId = campId,
            StartsAt = DateTime.UtcNow.AddDays(1), EndsAt = DateTime.UtcNow.AddDays(1).AddHours(2), Capacity = 5,
        });
        tdb.Db.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default));
        Assert.Contains("khung giờ", ex.Message);

        using var check = tdb.NewContext();
        Assert.Equal(FarFuture, (await check.Campaigns.AsNoTracking().SingleAsync(c => c.Id == campId)).StartsAt);
        Assert.Empty(await check.AuditLogs.Where(a => a.EntityId == campId).ToListAsync());
    }

    // ── (5) rubric_version KHÔNG đổi, tiêu chí KHÔNG đụng ────────────────────────────────────
    [Fact]
    public async Task RubricVersion_khong_doi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner, rubricVersion: 3, withCriterion: true);

        var res = await NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default);
        Assert.Equal(3, res.RubricVersion);

        using var check = tdb.NewContext();
        var camp = await check.Campaigns.AsNoTracking().SingleAsync(c => c.Id == campId);
        Assert.Equal(3, camp.RubricVersion);
        Assert.Null(camp.RubricVersionUpdatedAt);
        Assert.Equal(1, await check.CampaignCriteria.CountAsync(c => c.CampaignId == campId));
        Assert.Empty(await check.AuditLogs
            .Where(a => a.EntityId == campId && a.Action == AuditAction.EditCriteria).ToListAsync());
    }

    // ── (6) Ngoài org ⇒ KeyNotFoundException (→ 404) ────────────────────────────────────────
    [Fact]
    public async Task Ngoai_org_thi_404()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewSvc(tdb.NewContext()).StartEarlyAsync(Guid.NewGuid() /* org khác */, Guid.NewGuid(), campId, default));
    }

    // ── (7) KHÔNG đụng expires_at ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Khong_dung_expiresAt()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var exp = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var campId = Seed(tdb, owner, expiresAt: exp);

        var res = await NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default);

        Assert.Equal(exp, res.ExpiresAt);
        Assert.Equal(exp, (await tdb.NewContext().Campaigns.AsNoTracking().SingleAsync(c => c.Id == campId)).ExpiresAt);
    }

    // ── (8) Outbox "mở sớm": 1 row / lời mời CHƯA revoke; payload Kind=OpenedEarly, mốc cũ, không token ─
    [Fact]
    public async Task Chen_outbox_mo_som_cho_moi_loi_moi_chua_revoke()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = Seed(tdb, owner);
        AddInvitation(tdb, campId, "live1@x.com");
        AddInvitation(tdb, campId, "live2@x.com");
        AddInvitation(tdb, campId, "revoked@x.com", revokedAt: DateTime.UtcNow);
        tdb.Db.SaveChanges();

        await NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, campId, default);

        using var check = tdb.NewContext();
        var rows = await check.OutboxMessages.Where(m => m.CampaignId == campId).ToListAsync();
        Assert.Equal(2, rows.Count);   // chỉ 2 lời mời chưa revoke

        var emails = new List<string>();
        foreach (var r in rows)
        {
            var job = JsonSerializer.Deserialize<InvitationEmailJob>(r.Payload, JsonCi)!;
            Assert.Equal("OpenedEarly", job.Kind);
            Assert.Equal(FarFuture, job.PreviousStartsAt);
            Assert.Equal(string.Empty, job.Token);          // DB chỉ giữ hash — không có token thô
            Assert.Equal(campId, job.CampaignId);
            emails.Add(job.Email);
        }
        Assert.Equal(new[] { "live1@x.com", "live2@x.com" }, emails.OrderBy(e => e));
    }
}
