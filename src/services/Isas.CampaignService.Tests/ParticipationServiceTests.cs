using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// D2 — Distribution: invitation → join → membership → my-campaigns → start.
/// AuthProvisionClient + CampaignSessionClient mock; CampaignDbContext SQLite thật.
/// DB16 — membership sống ở bảng riêng <c>campaign_membership</c> (tách khỏi <c>cv_submission</c>).
/// </summary>
public class ParticipationServiceTests
{
    private static readonly Guid FixedCandidate = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FixedSession = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ParticipationService NewService(
        CampaignDbContext db,
        Mock<IAuthProvisionClient>? auth = null,
        Mock<ICampaignSessionClient>? session = null)
    {
        auth ??= DefaultAuth();
        session ??= DefaultSession();
        return new ParticipationService(db, auth.Object, session.Object, NullLogger<ParticipationService>.Instance);
    }

    private static Mock<IAuthProvisionClient> DefaultAuth()
    {
        var m = new Mock<IAuthProvisionClient>();
        // Deterministic: mọi email → cùng candidateId (mô phỏng create-or-get bên Auth).
        m.Setup(x => x.ProvisionCandidateAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisionedCandidate(FixedCandidate, "jwt-candidate-token"));
        return m;
    }

    private static Mock<ICampaignSessionClient> DefaultSession()
    {
        var m = new Mock<ICampaignSessionClient>();
        // Idempotent: mọi lần gọi → CÙNG sessionId (mô phỏng create-or-get bên Interview).
        m.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignSessionResult(FixedSession, new List<SessionQuestion>
            {
                new(Guid.NewGuid(), 1, "Q1", 120)
            }));
        return m;
    }

    private static CampaignInvitation NewInvitation(
        Guid campaignId, string email = "cand@acme.test",
        Guid? campaignCandidateId = null, DateTime? expiresAt = null, DateTime? revokedAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CampaignCandidateId = campaignCandidateId,
            Token = Guid.NewGuid().ToString("N"),
            Email = email,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
            CreatedAt = DateTime.UtcNow
        };

    // ── GET /invitations/{token} — metadata ────────────────────────────────────────
    [Fact]
    public async Task Metadata_OK_KhongTaoMembershipHoacSession()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.JDText = "JD nội dung";
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Communication",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var inv = NewInvitation(camp.Id);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var meta = await svc.GetInvitationMetadataAsync(inv.Token, default);

        Assert.Equal(camp.Id, meta.CampaignId);
        Assert.Equal("BE", meta.JobTitle);
        Assert.Equal("JD nội dung", meta.Description);
        Assert.Single(meta.Criteria);

        // KHÔNG side-effect: membership vẫn rỗng
        using var check = tdb.NewContext();
        Assert.Empty(await check.CampaignMemberships.ToListAsync());
    }

    [Fact]
    public async Task Metadata_TokenKhongTonTai_404()
    {
        using var tdb = new CampaignTestDb();
        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.GetInvitationMetadataAsync("khong-ton-tai", default));
    }

    [Fact]
    public async Task Metadata_Revoked_410()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var inv = NewInvitation(camp.Id, revokedAt: DateTime.UtcNow.AddMinutes(-1));
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvitationGoneException>(() =>
            svc.GetInvitationMetadataAsync(inv.Token, default));
    }

    [Fact]
    public async Task Metadata_HetHan_410()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var inv = NewInvitation(camp.Id, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvitationGoneException>(() =>
            svc.GetInvitationMetadataAsync(inv.Token, default));
    }

    [Fact]
    public async Task Metadata_CampaignKhongActive_410()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Closed);
        var inv = NewInvitation(camp.Id);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvitationGoneException>(() =>
            svc.GetInvitationMetadataAsync(inv.Token, default));
    }

    // ── POST /invitations/{token}/join ─────────────────────────────────────────────
    [Fact]
    public async Task Join_Duong1_TaoMembershipJoined_TraTokenVaJoined()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var inv = NewInvitation(camp.Id, "duong1@acme.test");
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var res = await svc.JoinCampaignAsync(inv.Token, default);

        Assert.Equal("jwt-candidate-token", res.AccessToken);
        Assert.Equal(FixedCandidate, res.CandidateId);
        Assert.Equal("Joined", res.MembershipStatus);

        using var check = tdb.NewContext();
        var membership = await check.CampaignMemberships.SingleAsync(m => m.CampaignId == camp.Id);
        Assert.Equal(FixedCandidate, membership.CandidateId);
        Assert.Equal(MembershipStatus.Joined, membership.Status);
        Assert.NotNull(membership.JoinedAt);
        Assert.Null(membership.CvSubmissionId);   // đường 1 (email) — không có CV shortlist
        // Đường 1 KHÔNG tạo cv_submission (chỉ membership).
        Assert.Empty(await check.CvSubmissions.ToListAsync());
    }

    // DB16 — đường 2 (shortlist): join KHÔNG lật row CV, tạo membership riêng trỏ về CV. HAI ROW là
    // hành vi CHỦ ĐÍCH của split (trước bảng God chỉ lật status 1 row Invited→Joined, mất sự thật sàng CV).
    [Fact]
    public async Task Join_Duong2_KhongLatRowCV_TaoMembershipTroVeCV()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var cvRow = new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            Email = "shortlisted@acme.test",
            ParseStatus = CvParseStatus.Done,
            Status = CvSubmissionStatus.Invited,
            Summary = "CV tốt",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var inv = NewInvitation(camp.Id, "shortlisted@acme.test", campaignCandidateId: cvRow.Id);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CvSubmissions.Add(cvRow);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await svc.JoinCampaignAsync(inv.Token, default);

        using var check = tdb.NewContext();
        // 1 cv_submission — GIỮ NGUYÊN sự thật sàng CV (Status Invited, Summary còn nguyên).
        var cvRows = await check.CvSubmissions.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Single(cvRows);
        Assert.Equal(cvRow.Id, cvRows[0].Id);
        Assert.Equal(CvSubmissionStatus.Invited, cvRows[0].Status);   // KHÔNG bị lật sang Joined
        Assert.Equal("CV tốt", cvRows[0].Summary);                    // dữ liệu sàng CV giữ nguyên

        // 1 campaign_membership MỚI — trỏ về CV shortlist + gắn candidate.
        var memberships = await check.CampaignMemberships.Where(m => m.CampaignId == camp.Id).ToListAsync();
        Assert.Single(memberships);
        Assert.Equal(MembershipStatus.Joined, memberships[0].Status);
        Assert.Equal(cvRow.Id, memberships[0].CvSubmissionId);
        Assert.Equal(FixedCandidate, memberships[0].CandidateId);
    }

    [Fact]
    public async Task Join_HaiLan_ChiMotMembership()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var inv = NewInvitation(camp.Id, "twice@acme.test");
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        await NewService(tdb.NewContext()).JoinCampaignAsync(inv.Token, default);
        await NewService(tdb.NewContext()).JoinCampaignAsync(inv.Token, default);

        using var check = tdb.NewContext();
        Assert.Single(await check.CampaignMemberships.Where(m => m.CampaignId == camp.Id).ToListAsync());
    }

    [Fact]
    public async Task Join_Revoked_410()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var inv = NewInvitation(camp.Id, revokedAt: DateTime.UtcNow.AddMinutes(-1));
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.Add(inv);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvitationGoneException>(() => svc.JoinCampaignAsync(inv.Token, default));

        using var check = tdb.NewContext();
        Assert.Empty(await check.CampaignMemberships.ToListAsync());   // không provision/không membership
    }

    // ── GET /my-campaigns ──────────────────────────────────────────────────────────
    [Fact]
    public async Task MyCampaigns_ChiCampaignDaJoin_InterviewStatusNotStarted()
    {
        using var tdb = new CampaignTestDb();
        var joined = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        joined.Domain = "FE";
        var other = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.AddRange(joined, other);
        // membership của candidate ở "joined"
        tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(joined.Id, FixedCandidate));
        // membership của candidate KHÁC ở "other" (không được liệt kê)
        tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(other.Id, Guid.NewGuid()));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var list = await svc.GetMyCampaignsAsync(FixedCandidate, default);

        Assert.Single(list);
        Assert.Equal(joined.Id, list[0].CampaignId);
        Assert.Equal("FE", list[0].JobTitle);
        Assert.Equal("Joined", list[0].MembershipStatus);
        Assert.Equal("NotStarted", list[0].InterviewStatus);
    }

    // ── POST /campaign/{id}/start ──────────────────────────────────────────────────
    [Fact]
    public async Task Start_MembershipJoined_TaoSession_SetSessionId_InProgress()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var res = await svc.StartInterviewAsync(FixedCandidate, camp.Id, default);

        Assert.Equal(FixedSession, res.SessionId);
        Assert.Equal(camp.Id, res.CampaignId);
        Assert.NotEmpty(res.Questions);

        using var check = tdb.NewContext();
        var membership = await check.CampaignMemberships.SingleAsync(m => m.CampaignId == camp.Id);
        Assert.Equal(FixedSession, membership.SessionId);
        Assert.Equal(InterviewProgressStatus.InProgress, membership.InterviewStatus);
    }

    [Fact]
    public async Task Start_ChuaJoin_403()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.StartInterviewAsync(FixedCandidate, camp.Id, default));
    }

    [Fact]
    public async Task Start_HaiLan_CungSessionId_Idempotent()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        var first = await NewService(tdb.NewContext()).StartInterviewAsync(FixedCandidate, camp.Id, default);
        var second = await NewService(tdb.NewContext()).StartInterviewAsync(FixedCandidate, camp.Id, default);

        Assert.Equal(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task Start_DaCompleted_409()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        var membership = Membership(camp.Id, FixedCandidate);
        membership.InterviewStatus = InterviewProgressStatus.Completed;
        membership.SessionId = FixedSession;
        tdb.Db.CampaignMemberships.Add(membership);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StartInterviewAsync(FixedCandidate, camp.Id, default));
    }

    // ── D3: Resume — mở lại campaign (start lại) → ĐÚNG session cũ, trạng thái KHÔNG hạ cấp ──────
    // Create-or-get (Interview dedup) đảm bảo cùng session_id; phía Campaign phải giữ SessionId ổn định
    // và KHÔNG reset InterviewStatus InProgress → NotStarted. (Chi tiết câu-đã-nộp do Interview phục vụ.)

    // D3(a): start 2× cùng candidate → membership.SessionId ổn định + InterviewStatus vẫn InProgress
    // (khác Start_HaiLan_CungSessionId_Idempotent: test này kiểm chứng STATE membership, không chỉ return).
    [Fact]
    public async Task Start_HaiLan_MembershipSessionOnDinh_KhongHaCap()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        await NewService(tdb.NewContext()).StartInterviewAsync(FixedCandidate, camp.Id, default);
        var second = await NewService(tdb.NewContext()).StartInterviewAsync(FixedCandidate, camp.Id, default);

        Assert.Equal(FixedSession, second.SessionId);

        using var check = tdb.NewContext();
        var membership = await check.CampaignMemberships.SingleAsync(m => m.CampaignId == camp.Id);
        Assert.Equal(FixedSession, membership.SessionId);                                  // session cũ giữ nguyên
        Assert.Equal(InterviewProgressStatus.InProgress, membership.InterviewStatus);      // KHÔNG reset về NotStarted
    }

    // D3(b): membership đang làm dở (InProgress + đã gắn session) → start lại (resume) → giữ nguyên
    // session + InProgress (không tạo/không đổi session, không hạ trạng thái).
    [Fact]
    public async Task Start_ResumeTuInProgress_GiuNguyenSessionVaTrangThai()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        var membership = Membership(camp.Id, FixedCandidate);
        membership.SessionId = FixedSession;
        membership.InterviewStatus = InterviewProgressStatus.InProgress;   // đã bắt đầu, trả lời dở
        tdb.Db.CampaignMemberships.Add(membership);
        await tdb.Db.SaveChangesAsync();

        var res = await NewService(tdb.NewContext()).StartInterviewAsync(FixedCandidate, camp.Id, default);

        Assert.Equal(FixedSession, res.SessionId);   // resume đúng session đang dở

        using var check = tdb.NewContext();
        var after = await check.CampaignMemberships.SingleAsync(m => m.CampaignId == camp.Id);
        Assert.Equal(FixedSession, after.SessionId);
        Assert.Equal(InterviewProgressStatus.InProgress, after.InterviewStatus);
    }

    // D3(c): sau start, GET /my-campaigns/{id} surface trạng thái resume — Started=true + SessionId khớp
    // + InterviewStatus=InProgress (FE biết đang dở → cho "tiếp tục").
    [Fact]
    public async Task MyCampaignDetail_SauStart_Started_SessionIdKhop_InProgress()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        await NewService(tdb.NewContext()).StartInterviewAsync(FixedCandidate, camp.Id, default);

        var detail = await NewService(tdb.NewContext())
            .GetCandidateCampaignAsync(FixedCandidate, camp.Id, default);

        Assert.True(detail.Started);
        Assert.Equal(FixedSession, detail.SessionId);
        Assert.Equal("InProgress", detail.InterviewStatus);
        Assert.Equal("Joined", detail.MembershipStatus);
    }

    // ── BK18: Campaign gửi expires_at khi tạo session B2B → Interview set session.Deadline (I2) ──────
    // Trước BK18 B2B Deadline=null (sweeper I2 không quét). StartInterview PHẢI truyền campaign.ExpiresAt
    // xuống CreateOrGetSessionAsync để Interview map → session.Deadline (auto-submit/abandon quá hạn).

    // BK18(a): campaign có hạn → truyền đúng campaign.ExpiresAt xuống session client.
    [Fact]
    public async Task Start_TruyenCampaignExpiresAt_XuongSessionClient()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        var deadline = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        camp.ExpiresAt = deadline;
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        var session = DefaultSession();
        await NewService(tdb.NewContext(), session: session)
            .StartInterviewAsync(FixedCandidate, camp.Id, default);

        session.Verify(x => x.CreateOrGetSessionAsync(
            FixedCandidate, camp.Id, It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            deadline, It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // BK18(b): campaign không đặt hạn (ExpiresAt null) → truyền null (không hard-deadline).
    [Fact]
    public async Task Start_ExpiresAtNull_TruyenNull_KhongHardDeadline()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);   // ExpiresAt = null
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        var session = DefaultSession();
        await NewService(tdb.NewContext(), session: session)
            .StartInterviewAsync(FixedCandidate, camp.Id, default);

        session.Verify(x => x.CreateOrGetSessionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            (DateTime?)null, It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // BK18(c): resume (start 2×) → vẫn truyền đúng expiresAt cả 2 lần (không đổi session).
    [Fact]
    public async Task Start_HaiLan_VanTruyenDungExpiresAt_KhongDoiSession()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        var deadline = new DateTime(2026, 9, 15, 8, 30, 0, DateTimeKind.Utc);
        camp.ExpiresAt = deadline;
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        var session = DefaultSession();
        var first = await NewService(tdb.NewContext(), session: session)
            .StartInterviewAsync(FixedCandidate, camp.Id, default);
        var second = await NewService(tdb.NewContext(), session: session)
            .StartInterviewAsync(FixedCandidate, camp.Id, default);

        Assert.Equal(first.SessionId, second.SessionId);   // resume → cùng session
        session.Verify(x => x.CreateOrGetSessionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            deadline, It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // BK14: Start truyền đúng campaign.OrgId xuống session client (Interview reserve owner=Org).
    [Fact]
    public async Task Start_TruyenCampaignOrgId_XuongSessionClient()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        var session = DefaultSession();
        await NewService(tdb.NewContext(), session: session)
            .StartInterviewAsync(FixedCandidate, camp.Id, default);

        session.Verify(x => x.CreateOrGetSessionAsync(
            FixedCandidate, camp.Id, camp.OrgId, It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // INT-17: Start PHẢI truyền toggle + trần adaptive của campaign xuống session client.
    // Trước fix, CampaignService không gửi gì ⇒ B2B adaptive không bao giờ bật được (E2E 2026-07-18).
    [Fact]
    public async Task Start_TruyenCampaignAdaptive_XuongSessionClient()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        camp.AdaptiveEnabled = true;
        camp.MaxFollowUps = 2;
        camp.MaxQuestions = 8;
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        var session = DefaultSession();
        var res = await NewService(tdb.NewContext(), session: session)
            .StartInterviewAsync(FixedCandidate, camp.Id, default);

        session.Verify(x => x.CreateOrGetSessionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            It.IsAny<DateTime?>(), true, 2, 8, It.IsAny<CancellationToken>()), Times.Once);

        // Cờ cũng surface về FE để trang thi biết sẽ có đuôi thích ứng.
        Assert.True(res.AdaptiveEnabled);
    }

    // INT-17: campaign KHÔNG bật → truyền false + null (Interview giữ luồng batch tĩnh cũ).
    [Fact]
    public async Task Start_CampaignKhongBatAdaptive_TruyenFalse()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);   // AdaptiveEnabled mặc định false
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        var session = DefaultSession();
        var res = await NewService(tdb.NewContext(), session: session)
            .StartInterviewAsync(FixedCandidate, camp.Id, default);

        session.Verify(x => x.CreateOrGetSessionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            It.IsAny<DateTime?>(), false, null, null, It.IsAny<CancellationToken>()), Times.Once);

        Assert.False(res.AdaptiveEnabled);
    }

    // BK14: ví org hết credit → session client ném InsufficientOrgCreditException → Start propagate
    // (controller map → 402); KHÔNG nuốt thành 502.
    [Fact]
    public async Task Start_ViOrgHetCredit_NemInsufficientOrgCredit()
    {
        using var tdb = new CampaignTestDb();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        tdb.Db.CampaignMemberships.Add(Membership(camp.Id, FixedCandidate));
        await tdb.Db.SaveChangesAsync();

        var session = new Mock<ICampaignSessionClient>();
        session.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientOrgCreditException("Tổ chức không đủ credit"));

        await Assert.ThrowsAsync<InsufficientOrgCreditException>(() =>
            NewService(tdb.NewContext(), session: session)
                .StartInterviewAsync(FixedCandidate, camp.Id, default));
    }

    // ── helpers ────────────────────────────────────────────────────────────────────
    private static Campaign ActiveCampaignWithQuestionAndCriterion(CampaignTestDb tdb)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.Questions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId,
            QuestionText = "Giải thích DI?", Source = QuestionSource.CustomHr,
            IsRequired = true, CreatedAt = DateTime.UtcNow
        });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Communication",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.Campaigns.Add(camp);
        return camp;
    }

    private static CampaignMembership Membership(Guid campaignId, Guid candidateId)
        => CampaignTestDb.NewMembership(campaignId, candidateId);
}
