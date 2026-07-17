using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// D4 — phát lại lời mời: POST /campaign/{id}/invitations/{invId}/reissue.
/// Vô hiệu token cũ (revoke → GET/join 410) + tạo invitation mới token khác + resend email + audit.
/// </summary>
public class CampaignInvitationReissueTests
{
    private static CampaignSvc NewService(CampaignDbContext db, IInvitationEmailPublisher? emailPublisher = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), emailPublisher ?? Mock.Of<IInvitationEmailPublisher>());

    // ParticipationService để kiểm chứng token cũ → 410 (đường GET metadata không gọi Auth/Session).
    private static ParticipationService NewParticipation(CampaignDbContext db) =>
        new(db, Mock.Of<IAuthProvisionClient>(), Mock.Of<ICampaignSessionClient>(),
            NullLogger<ParticipationService>.Instance);

    private static CampaignInvitation SeedInvitation(
        CampaignDbContext db, Guid campaignId, string email = "cand@acme.test", Guid? campaignCandidateId = null)
    {
        var inv = new CampaignInvitation
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CampaignCandidateId = campaignCandidateId,
            Token = Guid.NewGuid().ToString("N"),
            Email = email,
            CreatedAt = DateTime.UtcNow
        };
        db.CampaignInvitations.Add(inv);
        return inv;
    }

    // (a) happy path: token cũ RevokedAt set · invitation mới token khác · email publish · audit row
    [Fact]
    public async Task Reissue_RevokeCu_TaoMoiTokenKhac_DayEmail_GhiAudit()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var old = SeedInvitation(tdb.Db, camp.Id, "reissue@example.com");
        await tdb.Db.SaveChangesAsync();
        var oldToken = old.Token;

        var publisher = new Mock<IInvitationEmailPublisher>();
        var svc = NewService(tdb.NewContext(), publisher.Object);

        var result = await svc.ReissueInvitationAsync(owner, owner, camp.Id, old.Id, default);

        // response = lời mời mới
        Assert.Equal("reissue@example.com", result.Email);
        Assert.NotEqual(old.Id, result.Id);

        using var check = tdb.NewContext();
        var rows = await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(2, rows.Count);   // cũ (revoked) + mới

        var revoked = rows.Single(i => i.Id == old.Id);
        Assert.NotNull(revoked.RevokedAt);   // token cũ đã vô hiệu

        var fresh = rows.Single(i => i.Id == result.Id);
        Assert.Null(fresh.RevokedAt);
        Assert.NotEqual(oldToken, fresh.Token);   // token mới khác token cũ
        Assert.NotNull(fresh.SentAt);             // đã resend
        Assert.Equal("reissue@example.com", fresh.Email);

        // DB2b — 1 outbox-row cho lời mời mới (ghi CÙNG transaction; KHÔNG publish trực tiếp)
        var outbox = await check.OutboxMessages.ToListAsync();
        Assert.Single(outbox);
        Assert.Equal(result.Id, outbox[0].InvitationId);
        Assert.Null(outbox[0].PublishedAt);
        publisher.Verify(p => p.PublishAsync(It.IsAny<InvitationEmailJob>(), It.IsAny<CancellationToken>()), Times.Never);

        // audit ReissueInvitation
        var audit = await check.AuditLogs
            .Where(a => a.EntityId == camp.Id && a.Action == AuditAction.ReissueInvitation)
            .ToListAsync();
        Assert.Single(audit);
        Assert.Equal(owner, audit[0].ActorUserId);
        Assert.Equal(owner, audit[0].OrgId);
    }

    // (b) token cũ sau reissue → GET metadata = 410 (InvitationGoneException)
    [Fact]
    public async Task Reissue_TokenCu_GetMetadata_410()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var old = SeedInvitation(tdb.Db, camp.Id);
        await tdb.Db.SaveChangesAsync();
        var oldToken = old.Token;

        await NewService(tdb.NewContext()).ReissueInvitationAsync(owner, owner, camp.Id, old.Id, default);

        // token cũ dùng lại (GET metadata) → 410 Gone
        var participation = NewParticipation(tdb.NewContext());
        await Assert.ThrowsAsync<InvitationGoneException>(() =>
            participation.GetInvitationMetadataAsync(oldToken, default));

        // token mới dùng được (không revoke, campaign Active) → không ném
        using var read = tdb.NewContext();
        var freshToken = (await read.CampaignInvitations
            .Where(i => i.CampaignId == camp.Id && i.RevokedAt == null).SingleAsync()).Token;
        var meta = await NewParticipation(tdb.NewContext()).GetInvitationMetadataAsync(freshToken, default);
        Assert.Equal(camp.Id, meta.CampaignId);
    }

    // (c) ngoài org → 404 (KeyNotFoundException), không revoke/không tạo mới
    [Fact]
    public async Task Reissue_NgoaiOrg_404()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var old = SeedInvitation(tdb.Db, camp.Id);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.ReissueInvitationAsync(other, other, camp.Id, old.Id, default));

        // không tác dụng phụ: vẫn 1 row, chưa revoke
        using var check = tdb.NewContext();
        var rows = await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Null(rows[0].RevokedAt);
    }

    // (d) invitation không thuộc campaign → 404
    [Fact]
    public async Task Reissue_InvitationKhongThuocCampaign_404()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.ReissueInvitationAsync(owner, owner, camp.Id, Guid.NewGuid(), default));
    }

    // (e) campaign Closed → 409 (InvalidOperationException), không revoke/không tạo mới
    [Fact]
    public async Task Reissue_CampaignClosed_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Closed);
        tdb.Db.Campaigns.Add(camp);
        var old = SeedInvitation(tdb.Db, camp.Id);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ReissueInvitationAsync(owner, owner, camp.Id, old.Id, default));

        using var check = tdb.NewContext();
        var rows = await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Null(rows[0].RevokedAt);
    }

    // (f) reissue 2 lần (chuỗi) → mỗi lần 1 token mới; các token cũ đều vô hiệu (410)
    [Fact]
    public async Task Reissue_HaiLan_MoiLanTokenMoi_CuDeuVoHieu()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var a = SeedInvitation(tdb.Db, camp.Id);
        await tdb.Db.SaveChangesAsync();
        var tokenA = a.Token;

        // lần 1: A → B
        var b = await NewService(tdb.NewContext()).ReissueInvitationAsync(owner, owner, camp.Id, a.Id, default);
        // lần 2: B → C
        var c = await NewService(tdb.NewContext()).ReissueInvitationAsync(owner, owner, camp.Id, b.Id, default);

        using var check = tdb.NewContext();
        var all = await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(3, all.Count);

        // 3 token phân biệt
        Assert.Equal(3, all.Select(i => i.Token).Distinct().Count());

        // chỉ C (mới nhất) còn hiệu lực; A và B đều revoked
        Assert.NotNull(all.Single(i => i.Id == a.Id).RevokedAt);
        Assert.NotNull(all.Single(i => i.Id == b.Id).RevokedAt);
        Assert.Null(all.Single(i => i.Id == c.Id).RevokedAt);

        // token A và token B đều → 410
        var tokenB = all.Single(i => i.Id == b.Id).Token;
        await Assert.ThrowsAsync<InvitationGoneException>(() =>
            NewParticipation(tdb.NewContext()).GetInvitationMetadataAsync(tokenA, default));
        await Assert.ThrowsAsync<InvitationGoneException>(() =>
            NewParticipation(tdb.NewContext()).GetInvitationMetadataAsync(tokenB, default));
    }

    // (g) đường 2 (shortlist): reissue giữ campaign_candidate_id trên lời mời mới
    [Fact]
    public async Task Reissue_Duong2_GiuCampaignCandidateId()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        // DB9: campaign_invitations.campaign_candidate_id → campaign_candidates.id (FK). Seed candidate
        // THẬT để invitation đường-2 (shortlist) trỏ tới row tồn tại (đúng ngữ nghĩa + thoả FK).
        var candidateRowId = Guid.NewGuid();
        tdb.Db.CampaignCandidates.Add(new CampaignCandidate
        {
            Id = candidateRowId, CampaignId = camp.Id, Email = "shortlist@example.com",
            ParseStatus = CvParseStatus.Done, Status = CandidateStatus.Analyzed,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var old = SeedInvitation(tdb.Db, camp.Id, "shortlist@example.com", campaignCandidateId: candidateRowId);
        await tdb.Db.SaveChangesAsync();

        var result = await NewService(tdb.NewContext()).ReissueInvitationAsync(owner, owner, camp.Id, old.Id, default);

        using var check = tdb.NewContext();
        var fresh = await check.CampaignInvitations.SingleAsync(i => i.Id == result.Id);
        Assert.Equal(candidateRowId, fresh.CampaignCandidateId);
    }
}
