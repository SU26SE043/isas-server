using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCP1-B5 (Campaign) — ghim bó biến RAW vào campaign_rankings lúc chấm; ghim
/// cv_submission.scoring_policy_version LÚC ĐẨY JOB SÀNG (retry giữ, rescreen re-pin); và gửi hợp
/// đồng chấm điểm (biểu thức) xuống buổi thi qua ParticipationService.
/// </summary>
public class ScoringPolicyPinB5Tests
{
    // ── RankingEventHandler — bó biến RAW đến qua event ───────────────────────────────────────
    private static RankingEventHandler NewHandler(CampaignDbContext db) =>
        new(db, Mock.Of<ILogger<RankingEventHandler>>());

    private static ScoringInputsSnapshot Bag(decimal pct) => new(
        new[] { new CriterionInputSnapshot("Communication", pct, 1.0m, 5) }, Answered: 3, TotalQuestions: 4);

    private static SessionScoredMessage Msg(Guid campaignId, Guid sessionId, ScoringInputsSnapshot? bag) => new()
    {
        SessionId = sessionId,
        CampaignId = campaignId,
        CandidateId = Guid.NewGuid(),
        TotalScore = 72m,
        ScoredAt = DateTime.UtcNow,
        RubricVersion = 1,
        ScoringInputs = bag,
    };

    [Fact]
    public async Task Ranking_ghi_bo_bien_RAW_luc_tao_va_upsert()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();
        var sessionId = Guid.NewGuid();

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(Msg(camp.Id, sessionId, Bag(80m)));

        using (var check = tdb.NewContext())
        {
            var row = await check.CampaignRankings.SingleAsync(r => r.SessionId == sessionId);
            Assert.NotNull(row.ScoringInputs);
            Assert.Equal(3, row.ScoringInputs!.Answered);
            Assert.Equal(4, row.ScoringInputs.TotalQuestions);
            var c = Assert.Single(row.ScoringInputs.Criteria);
            Assert.Equal("Communication", c.Name);
            Assert.Equal(80m, c.Pct);
            Assert.Equal(1.0m, c.Weight);
            Assert.Equal(5, c.MaxScore);
        }

        // Upsert: event tới lần nữa với bó khác → row cập nhật (event là nguồn quyền lực).
        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(Msg(camp.Id, sessionId, Bag(55m)));

        using var check2 = tdb.NewContext();
        Assert.Single(check2.CampaignRankings.Where(r => r.SessionId == sessionId));
        var row2 = await check2.CampaignRankings.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(55m, Assert.Single(row2.ScoringInputs!.Criteria).Pct);
    }

    [Fact]
    public async Task Ranking_ScoringInputs_null_khong_crash_consumer()
    {
        // CẤM #4 — event cũ / bản Interview cũ không mang bó biến ⇒ cột null, KHÔNG ném.
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();
        var sessionId = Guid.NewGuid();

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(Msg(camp.Id, sessionId, bag: null));

        using var check = tdb.NewContext();
        var row = await check.CampaignRankings.SingleAsync(r => r.SessionId == sessionId);
        Assert.Null(row.ScoringInputs);
        Assert.Equal(72m, row.TotalScore);   // phần còn lại vẫn ghi bình thường
    }

    // ── CvScreeningService — ghim/retry/rescreen ─────────────────────────────────────────────
    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:CallbackBase"] = "http://campaign:8080" })
            .Build();

    private static CvScreeningService NewCvService(CampaignDbContext db) =>
        new(db, Mock.Of<ICvScreeningPublisher>(), Config(), Mock.Of<ILogger<CvScreeningService>>());

    private static Campaign SeedCvCampaign(CampaignTestDb tdb, Guid owner, int? cvPolicyVersion)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.Domain = "BE";
        camp.JobNeeds = new List<JobNeed>
        {
            new() { NeedId = "need-1", Category = JobNeedCategories.Technical, Text = "Thạo .NET" },
        };
        camp.CvPolicyVersion = cvPolicyVersion;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    private static CvSubmission SeedCv(CampaignTestDb tdb, Guid campaignId, CvSubmissionStatus status,
        int? scoringPolicyVersion = null)
    {
        var now = DateTime.UtcNow;
        var cand = new CvSubmission
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Email = $"c{Guid.NewGuid():N}@x.com",
            CvParsedText = "CV text", ParseStatus = CvParseStatus.Done, Status = status,
            ScoringPolicyVersion = scoringPolicyVersion, CreatedAt = now, UpdatedAt = now,
        };
        tdb.Db.CvSubmissions.Add(cand);
        tdb.Db.SaveChanges();
        return cand;
    }

    [Fact]
    public async Task Publish_ghim_scoring_policy_version_luc_day_job()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCvCampaign(tdb, owner, cvPolicyVersion: 2);
        var cand = SeedCv(tdb, camp.Id, CvSubmissionStatus.Filtered);

        await NewCvService(tdb.NewContext()).PublishScreeningJobsAsync(owner, camp.Id, default);

        using var check = tdb.NewContext();
        var after = await check.CvSubmissions.SingleAsync(c => c.Id == cand.Id);
        Assert.Equal(2, after.ScoringPolicyVersion);
        Assert.Equal(CvSubmissionStatus.Analyzing, after.Status);
    }

    [Fact]
    public async Task Publish_lai_GIU_pin_cu_du_policy_da_doi_retry()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCvCampaign(tdb, owner, cvPolicyVersion: 5);   // con trỏ HIỆN HÀNH = 5
        // Ứng viên ĐÃ được ghim v2 ở lần đánh giá trước, giờ (mô phỏng republisher) lại về Filtered.
        var cand = SeedCv(tdb, camp.Id, CvSubmissionStatus.Filtered, scoringPolicyVersion: 2);

        await NewCvService(tdb.NewContext()).PublishScreeningJobsAsync(owner, camp.Id, default);

        using var check = tdb.NewContext();
        // `??=` : đã có pin ⇒ GIỮ v2 (retry = cùng một lần đánh giá), KHÔNG nhảy sang 5.
        Assert.Equal(2, (await check.CvSubmissions.SingleAsync(c => c.Id == cand.Id)).ScoringPolicyVersion);
    }

    [Fact]
    public async Task Rescreen_RE_PIN_theo_policy_hien_hanh()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCvCampaign(tdb, owner, cvPolicyVersion: 5);
        var cand = SeedCv(tdb, camp.Id, CvSubmissionStatus.Analyzed, scoringPolicyVersion: 2);   // lần đánh giá cũ

        await NewCvService(tdb.NewContext()).RescreenCandidateAsync(owner, camp.Id, cand.Id, default);

        using var check = tdb.NewContext();
        var after = await check.CvSubmissions.SingleAsync(c => c.Id == cand.Id);
        Assert.Equal(5, after.ScoringPolicyVersion);   // lần đánh giá MỚI → theo con trỏ hiện hành
        Assert.Equal(CvSubmissionStatus.Analyzing, after.Status);
    }

    // ── ParticipationService — gửi hợp đồng chấm xuống buổi thi ───────────────────────────────
    private static (CampaignScoringPolicyInput? Captured, Mock<ICampaignSessionClient> Mock) CapturingSession()
    {
        CampaignScoringPolicyInput? captured = null;
        var m = new Mock<ICampaignSessionClient>();
        m.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<SessionQuestionInput>?>(), It.IsAny<CampaignScoringPolicyInput?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid _, Guid _, string _, IReadOnlyList<string> _,
                    IReadOnlyList<SessionCriterionInput> _, DateTime? _, bool? _, int? _, int? _,
                    int? _, string _, int _, IReadOnlyList<SessionQuestionInput>? _,
                    CampaignScoringPolicyInput? p, CancellationToken _) => captured = p)
            .ReturnsAsync(new CampaignSessionResult(Guid.NewGuid(), new List<SessionQuestion>()));
        return (null, m);
    }

    private static Campaign SeedStartCampaign(CampaignTestDb tdb, Guid candidate, int? interviewPolicyVersion)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.Questions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId,
            QuestionText = "Giải thích DI?", Source = QuestionSource.CustomHr,
            IsRequired = true, CreatedAt = DateTime.UtcNow,
        });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Chuyên môn",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        camp.InterviewPolicyVersion = interviewPolicyVersion;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(camp.Id, candidate));
        if (interviewPolicyVersion is int v)
            tdb.Db.ScoringPolicies.Add(new ScoringPolicy
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, Kind = ScoringExpressionKind.Interview,
                Version = v, EngineVersion = "1", Name = "Bản HR",
                Expression = "weighted_avg_pct * completeness", PassScorePct = 55,
                CreatedAt = DateTime.UtcNow, CreatedBy = Guid.NewGuid(),
            });
        tdb.Db.SaveChanges();
        return camp;
    }

    [Fact]
    public async Task Start_gui_hop_dong_cham_KEM_bieu_thuc_xuong_buoi_thi()
    {
        using var tdb = new CampaignTestDb();
        var candidate = Guid.NewGuid();
        var camp = SeedStartCampaign(tdb, candidate, interviewPolicyVersion: 2);
        var (_, session) = CapturingSession();

        var svc = new ParticipationService(tdb.NewContext(), Mock.Of<IAuthProvisionClient>(),
            session.Object, NullLogger<ParticipationService>.Instance);
        await svc.StartInterviewAsync(candidate, camp.Id, default);

        session.Verify(x => x.CreateOrGetSessionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<IReadOnlyList<SessionQuestionInput>?>(),
            It.Is<CampaignScoringPolicyInput?>(p =>
                p != null && p.Version == 2 && p.Expression == "weighted_avg_pct * completeness"
                && p.PassScorePct == 55 && p.EngineVersion == "1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_campaign_khong_ap_chinh_sach_gui_null()
    {
        using var tdb = new CampaignTestDb();
        var candidate = Guid.NewGuid();
        var camp = SeedStartCampaign(tdb, candidate, interviewPolicyVersion: null);
        var (_, session) = CapturingSession();

        var svc = new ParticipationService(tdb.NewContext(), Mock.Of<IAuthProvisionClient>(),
            session.Object, NullLogger<ParticipationService>.Instance);
        await svc.StartInterviewAsync(candidate, camp.Id, default);

        session.Verify(x => x.CreateOrGetSessionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<IReadOnlyList<SessionQuestionInput>?>(),
            (CampaignScoringPolicyInput?)null,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
