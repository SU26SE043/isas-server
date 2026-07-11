using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// C15 — Distribution đường 2: POST /campaign/{id}/candidates/invite (mời hàng loạt từ shortlist sàng CV).
/// (a) Analyzed + email → invitation gắn campaign_candidate_id + Invited + publisher gọi;
/// (b) email null → failed[], status không đổi;
/// (c) đã Invited → skip (absorbing);
/// (d) không Analyzed → failed[];
/// (e) campaign chưa Active → 409; ngoài org → 404;
/// (f) vượt max_candidates → 400 (không tạo dở dang);
/// (g) email đã mời (đường 1) → failed[] (dedup).
/// Publisher mock (không cần broker); SQLite in-mem (CampaignTestDb).
/// </summary>
public class CampaignInviteShortlistTests
{
    private static CampaignSvc NewService(CampaignDbContext db, IInvitationEmailPublisher? emailPublisher = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), emailPublisher ?? Mock.Of<IInvitationEmailPublisher>());

    private static Campaign SeedActiveCampaign(CampaignTestDb tdb, Guid owner, int? maxCandidates = null)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.MaxCandidates = maxCandidates;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    private static CampaignCandidate SeedCandidate(
        CampaignTestDb tdb, Guid campaignId, CandidateStatus status, string? email)
    {
        var now = DateTime.UtcNow;
        var cand = new CampaignCandidate
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Email = email,
            CvParsedText = "CV text",
            CvFileUrl = $"campaigns/{campaignId}/candidates/x.pdf",
            ParseStatus = CvParseStatus.Done,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
        tdb.Db.CampaignCandidates.Add(cand);
        tdb.Db.SaveChanges();
        return cand;
    }

    // (a) Analyzed + email → tạo invitation gắn campaign_candidate_id + Invited + đẩy email queue.
    [Fact]
    public async Task Analyzed_co_email_tao_invitation_gan_candidateId_va_Invited()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var c1 = SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "a@x.com");
        var c2 = SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "b@x.com");

        var publisher = new Mock<IInvitationEmailPublisher>();
        var svc = NewService(tdb.NewContext(), publisher.Object);

        var result = await svc.InviteShortlistedCandidatesAsync(owner, owner, camp.Id, new() { c1.Id, c2.Id }, default);

        Assert.Equal(2, result.Invited.Count);
        Assert.Empty(result.Failed);
        publisher.Verify(p => p.PublishAsync(It.IsAny<InvitationEmailJob>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        using var check = tdb.NewContext();
        var invitations = await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(2, invitations.Count);
        Assert.All(invitations, i => Assert.NotNull(i.CampaignCandidateId));       // đường 2 → gắn candidate
        Assert.All(invitations, i => Assert.False(string.IsNullOrWhiteSpace(i.Token)));
        Assert.All(invitations, i => Assert.NotNull(i.SentAt));
        Assert.Contains(invitations, i => i.CampaignCandidateId == c1.Id);
        Assert.Contains(invitations, i => i.CampaignCandidateId == c2.Id);

        var cands = await check.CampaignCandidates.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.All(cands, c => Assert.Equal(CandidateStatus.Invited, c.Status));
        Assert.True(await check.AuditLogs.AnyAsync(a => a.Action == AuditAction.Invite && a.EntityId == camp.Id));
    }

    // (b) email null → nằm trong failed[]; status KHÔNG đổi; không tạo invitation.
    [Fact]
    public async Task Email_null_vao_failed_status_khong_doi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var withEmail = SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "a@x.com");
        var noEmail = SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, null);

        var svc = NewService(tdb.NewContext());
        var result = await svc.InviteShortlistedCandidatesAsync(owner, owner, camp.Id, new() { withEmail.Id, noEmail.Id }, default);

        Assert.Single(result.Invited);
        Assert.Equal(withEmail.Id, result.Invited[0].CandidateId);
        Assert.Single(result.Failed);
        Assert.Equal(noEmail.Id, result.Failed[0].CandidateId);
        Assert.Contains("email", result.Failed[0].Reason, StringComparison.OrdinalIgnoreCase);

        using var check = tdb.NewContext();
        Assert.Equal(CandidateStatus.Analyzed, (await check.CampaignCandidates.FindAsync(noEmail.Id))!.Status);
        Assert.Single(await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync());
    }

    // (c) đã Invited → skip (absorbing): không tạo invitation thứ 2, không vào failed.
    [Fact]
    public async Task Da_Invited_skip_khong_tao_invitation_moi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var invited = SeedCandidate(tdb, camp.Id, CandidateStatus.Invited, "a@x.com");

        var publisher = new Mock<IInvitationEmailPublisher>();
        var svc = NewService(tdb.NewContext(), publisher.Object);
        var result = await svc.InviteShortlistedCandidatesAsync(owner, owner, camp.Id, new() { invited.Id }, default);

        Assert.Empty(result.Invited);
        Assert.Empty(result.Failed);
        publisher.Verify(p => p.PublishAsync(It.IsAny<InvitationEmailJob>(), It.IsAny<CancellationToken>()), Times.Never);

        using var check = tdb.NewContext();
        Assert.Empty(await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync());
    }

    // (d) trạng thái khác Analyzed/Invited (vd Filtered) → failed[], không mời.
    [Fact]
    public async Task Chua_Analyzed_vao_failed()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var filtered = SeedCandidate(tdb, camp.Id, CandidateStatus.Filtered, "a@x.com");

        var svc = NewService(tdb.NewContext());
        var result = await svc.InviteShortlistedCandidatesAsync(owner, owner, camp.Id, new() { filtered.Id }, default);

        Assert.Empty(result.Invited);
        Assert.Single(result.Failed);
        Assert.Equal(filtered.Id, result.Failed[0].CandidateId);
        Assert.Contains("Analyzed", result.Failed[0].Reason);
    }

    // (e-1) campaign chưa Active → InvalidOperationException (409).
    [Fact]
    public async Task Campaign_chua_Active_nem_InvalidOperationException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();
        var cand = SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "a@x.com");

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.InviteShortlistedCandidatesAsync(owner, owner, camp.Id, new() { cand.Id }, default));
    }

    // (e-2) ngoài org → KeyNotFoundException (404).
    [Fact]
    public async Task Ngoai_org_nem_KeyNotFound()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var cand = SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "a@x.com");

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.InviteShortlistedCandidatesAsync(Guid.NewGuid() /* org khác */, Guid.NewGuid(), camp.Id, new() { cand.Id }, default));
    }

    // (f) vượt max_candidates → ArgumentException (400); KHÔNG tạo dở dang.
    [Fact]
    public async Task Vuot_max_candidates_nem_ArgumentException_khong_tao()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner, maxCandidates: 1);
        var c1 = SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "a@x.com");
        var c2 = SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "b@x.com");

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.InviteShortlistedCandidatesAsync(owner, owner, camp.Id, new() { c1.Id, c2.Id }, default));

        using var check = tdb.NewContext();
        Assert.Empty(await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync());
        Assert.All(await check.CampaignCandidates.Where(c => c.CampaignId == camp.Id).ToListAsync(),
            c => Assert.Equal(CandidateStatus.Analyzed, c.Status));   // không lật trạng thái khi vượt cap
    }

    // (g) email đã có invitation (đường 1 mời thẳng) → dedup → failed[].
    [Fact]
    public async Task Email_da_moi_duong1_vao_failed_dedup()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        tdb.Db.CampaignInvitations.Add(new CampaignInvitation
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            CampaignCandidateId = null,          // đường 1
            Token = "existing-token",
            Email = "a@x.com",
            CreatedAt = DateTime.UtcNow
        });
        tdb.Db.SaveChanges();
        var cand = SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "a@x.com");

        var svc = NewService(tdb.NewContext());
        var result = await svc.InviteShortlistedCandidatesAsync(owner, owner, camp.Id, new() { cand.Id }, default);

        Assert.Empty(result.Invited);
        Assert.Single(result.Failed);
        Assert.Contains("đã được mời", result.Failed[0].Reason);

        using var check = tdb.NewContext();
        Assert.Single(await check.CampaignInvitations.Where(i => i.CampaignId == camp.Id).ToListAsync());  // vẫn 1
        Assert.Equal(CandidateStatus.Analyzed, (await check.CampaignCandidates.FindAsync(cand.Id))!.Status);
    }

    // (h) candidateId không thuộc campaign → failed[] "không tìm thấy".
    [Fact]
    public async Task CandidateId_ngoai_campaign_vao_failed()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);

        var svc = NewService(tdb.NewContext());
        var ghost = Guid.NewGuid();
        var result = await svc.InviteShortlistedCandidatesAsync(owner, owner, camp.Id, new() { ghost }, default);

        Assert.Empty(result.Invited);
        Assert.Single(result.Failed);
        Assert.Equal(ghost, result.Failed[0].CandidateId);
    }
}
