using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Analytics;
using Isas.Shared.Scoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// <c>GET /campaign/analytics</c> — phân tích tuyển dụng theo TỔ CHỨC (employer). Khoá 6 bất biến của hợp
/// đồng: (1) org A không thấy org B ở MỌI khối; (2) campaign soft-delete không đếm; (3) Pass/Fail/undetermined
/// == kết luận của <c>GetCampaignResultsAsync</c> từng dòng (một helper dùng chung); (4) <c>invitations.*</c>
/// == đếm từ <c>GetInvitationsAsync</c> (cùng <c>ResolveDeliveryStatus</c> + cùng phép ghép join); (5) bucket
/// neo đúng mốc (<c>email_sent_at</c>, KHÔNG phải <c>created_at</c>); (6) đủ 5 band / 3 risk kể cả count 0,
/// median null khi rỗng chứ KHÔNG phải 0.
///
/// Serialize response THẬT bằng <c>JsonSerializerDefaults.Web</c> (= cấu hình controller) để khoá TÊN KHOÁ
/// camelCase — FE bind theo đúng tên này, lệch là field rụng im lặng.
/// </summary>
public class CampaignAnalyticsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly DateTime T0 = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly AnalyticsPeriodResult Period30d = new(T0, T0.AddDays(30), AnalyticsGranularity.Day);

    private static CampaignAnalyticsService NewService(CampaignDbContext db) => new(db);

    private static CampaignSvc NewCampaignService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static CampaignAnalyticsController NewController(CampaignDbContext db, Guid? orgId)
    {
        var controller = new CampaignAnalyticsController(NewService(db));
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (orgId is not null) claims.Add(new Claim("org_id", orgId.Value.ToString()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return controller;
    }

    // ── seed helpers ──────────────────────────────────────────────────────────────────────────────

    private static Campaign SeedCampaign(
        CampaignDbContext db, Guid orgId, CampaignStatus status = CampaignStatus.Active,
        int? passScorePct = null, DateTime? createdAt = null, string title = "Test Campaign",
        params CampaignCriterion[] criteria)
    {
        var c = CampaignTestDb.NewCampaign(orgId, status);
        c.Title = title;
        c.PassScorePct = passScorePct;
        c.CreatedAt = createdAt ?? T0.AddDays(1);
        c.UpdatedAt = c.CreatedAt;
        foreach (var cr in criteria) { cr.CampaignId = c.Id; c.Criteria.Add(cr); }
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static CampaignCriterion Criterion(string name, int? minPct = null, int order = 0)
        => new()
        {
            Id = Guid.NewGuid(), OrderNo = order, Name = name, Weight = 1.0m, MaxScore = 5, MinPct = minPct,
            Source = CriterionSource.HrEdited, CreatedAt = T0, UpdatedAt = T0,
        };

    private static ScoringInputsSnapshot Snap(string critName, decimal pct, Guid? critId)
        => new(new[] { new CriterionInputSnapshot(critName, pct, 1.0m, 5, CriterionId: critId) }, Answered: 5, TotalQuestions: 5);

    private static CampaignRanking SeedRanking(
        CampaignDbContext db, Guid campaignId, decimal totalScore, DateTime? updatedAt = null,
        decimal? overrideScore = null, string? overrideResult = null, ScoringInputsSnapshot? snapshot = null,
        Guid? sessionId = null)
    {
        var r = new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = Guid.NewGuid(),
            SessionId = sessionId ?? Guid.NewGuid(), TotalScore = totalScore, UpdatedAt = updatedAt ?? T0.AddDays(2),
            OverrideScore = overrideScore, OverrideResult = overrideResult, ScoringInputs = snapshot,
        };
        db.CampaignRankings.Add(r);
        db.SaveChanges();
        return r;
    }

    private static CvSubmission SeedSubmission(
        CampaignDbContext db, Guid campaignId, CvSubmissionStatus status = CvSubmissionStatus.Analyzed,
        int? fit = null, string? risk = null, List<string>? skills = null, string? email = null)
    {
        var s = new CvSubmission
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Email = email, ParseStatus = CvParseStatus.Done,
            Status = status, OverallMatchScore = fit, VerificationRisk = risk, Skills = skills,
            CreatedAt = T0, UpdatedAt = T0,
        };
        db.CvSubmissions.Add(s);
        db.SaveChanges();
        return s;
    }

    private static CampaignInvitation SeedInvitation(
        CampaignDbContext db, Guid campaignId, string email, DateTime? createdAt = null,
        DateTime? emailSentAt = null, DateTime? revokedAt = null, DateTime? expiresAt = null,
        Guid? campaignCandidateId = null)
    {
        var id = Guid.NewGuid();
        var inv = new CampaignInvitation
        {
            Id = id, CampaignId = campaignId, CampaignCandidateId = campaignCandidateId,
            TokenHash = InvitationTokens.Hash(id.ToString("N")), Email = email,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(30), SentAt = createdAt ?? T0,
            EmailSentAt = emailSentAt, RevokedAt = revokedAt, CreatedAt = createdAt ?? T0,
        };
        db.CampaignInvitations.Add(inv);
        db.SaveChanges();
        return inv;
    }

    private static CampaignMembership SeedMembership(
        CampaignDbContext db, Guid campaignId, DateTime? joinedAt = null, Guid? sessionId = null,
        InterviewProgressStatus? interviewStatus = null, DateTime? interviewStartedAt = null,
        Guid? invitationId = null, string? email = null, Guid? cvSubmissionId = null)
    {
        var m = new CampaignMembership
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = Guid.NewGuid(), CvSubmissionId = cvSubmissionId,
            InvitationId = invitationId, Email = email, Status = MembershipStatus.Joined,
            JoinedAt = joinedAt ?? T0.AddDays(1), SessionId = sessionId, InterviewStatus = interviewStatus,
            InterviewStartedAt = interviewStartedAt, CreatedAt = T0, UpdatedAt = T0,
        };
        db.CampaignMemberships.Add(m);
        db.SaveChanges();
        return m;
    }

    private static void SeedFlag(CampaignDbContext db, Guid campaignId, string signalType, Guid? sessionId = null)
    {
        db.SessionFlags.Add(new SessionFlag
        {
            Id = Guid.NewGuid(), SessionId = sessionId ?? Guid.NewGuid(), CampaignId = campaignId,
            CandidateId = Guid.NewGuid(), SignalType = signalType, DetectedAt = T0,
        });
        db.SaveChanges();
    }

    // ── route + auth (khoá bằng reflection — literal phải đứng độc lập với {id} của CampaignController) ──

    [Fact]
    public void Route_LiteralAnalytics_TrenPrefixCampaign_GateEmployer()
    {
        var t = typeof(CampaignAnalyticsController);
        Assert.Equal("campaign", t.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("Employer", t.GetCustomAttribute<AuthorizeAttribute>()!.Roles);

        var action = t.GetMethod(nameof(CampaignAnalyticsController.Analytics))!;
        var get = action.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(get);
        Assert.Equal("analytics", get!.Template);

        // Đối chứng: CampaignController vẫn có [HttpGet("{id}")] cùng prefix — chính cái mà literal phải thắng.
        var byId = typeof(CampaignController).GetMethod(nameof(CampaignController.GetCampaignById))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template;
        Assert.Equal("{id}", byId);
    }

    [Fact]
    public async Task ThieuOrgClaim_403()
    {
        using var t = new CampaignTestDb();
        var result = await NewController(t.Db, orgId: null).Analytics();
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task FromLonHonHoacBangTo_400()
    {
        using var t = new CampaignTestDb();
        var result = await NewController(t.Db, Guid.NewGuid()).Analytics(from: T0, to: T0);
        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("from", JsonSerializer.Serialize(bad.Value, Web));
    }

    [Fact]
    public async Task GroupByLa_400()
    {
        using var t = new CampaignTestDb();
        var result = await NewController(t.Db, Guid.NewGuid()).Analytics(from: T0, to: T0.AddDays(1), groupBy: "week");
        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("groupBy", JsonSerializer.Serialize(bad.Value, Web));
    }

    // ── org rỗng + bất biến 6 ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrgChuaCoCampaign_200_ToanSo0_MedianNull_Du5Band3Risk()
    {
        using var t = new CampaignTestDb();
        var result = await NewController(t.Db, Guid.NewGuid()).Analytics(from: T0, to: T0.AddDays(30), groupBy: "day");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var res = Assert.IsType<CampaignAnalyticsResponse>(ok.Value);

        Assert.Equal(0, res.Campaigns.Total);
        Assert.Empty(res.Campaigns.ByStatus);
        Assert.Equal(0, res.Screening.Submissions);
        Assert.Null(res.Screening.MedianFitScore);
        Assert.Null(res.Interviews.MedianScore);
        Assert.Equal(new[] { "0-19", "20-39", "40-59", "60-79", "80-100" }, res.Screening.FitDistribution.Select(b => b.Band));
        Assert.All(res.Screening.FitDistribution, b => Assert.Equal(0, b.Count));
        Assert.Equal(new[] { "0-19", "20-39", "40-59", "60-79", "80-100" }, res.Interviews.ScoreDistribution.Select(b => b.Band));
        Assert.Equal(new[] { "Low", "Medium", "High" }, res.Screening.RiskBySeverity.Select(r => r.Risk));
        Assert.All(res.Screening.RiskBySeverity, r => Assert.Equal(0, r.Count));
        Assert.Equal(0, res.Invitations.Total);
        Assert.Equal(0, res.Interviews.Joined);
        Assert.Empty(res.Buckets);
        Assert.Empty(res.PerCampaign);
        Assert.Equal("day", res.Granularity);

        // JSON thật: median phải là literal null, KHÔNG phải 0.
        var json = JsonSerializer.Serialize(ok.Value, Web);
        Assert.Contains("\"medianFitScore\":null", json);
        Assert.Contains("\"medianScore\":null", json);
    }

    [Fact]
    public async Task CoDuLieu_VanDu5Band3Risk_KeCaBandCount0()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org);
        SeedSubmission(t.Db, c.Id, fit: 62, risk: "High");
        SeedRanking(t.Db, c.Id, 55.5m);

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);

        Assert.Equal(5, res.Screening.FitDistribution.Count);
        Assert.Equal(new[] { 0, 0, 0, 1, 0 }, res.Screening.FitDistribution.Select(b => b.Count));
        Assert.Equal(3, res.Screening.RiskBySeverity.Count);
        Assert.Equal(new[] { 0, 0, 1 }, res.Screening.RiskBySeverity.Select(r => r.Count));
        Assert.Equal(new[] { 0, 0, 1, 0, 0 }, res.Interviews.ScoreDistribution.Select(b => b.Count));
        Assert.Equal(62m, res.Screening.MedianFitScore);
        Assert.Equal(55.5m, res.Interviews.MedianScore);
    }

    // ── bất biến 1: org A không thấy org B ở MỌI khối ─────────────────────────────────────────────

    // Supervisor mutation S1: `started` đếm theo `InterviewStatus != null` thay vì `SessionId != null` chạy qua
    // XANH vì mọi fixture đặt cả hai cùng lúc. Hợp đồng định nghĩa started = session_id != null — khoá bằng
    // hai dòng bất đối xứng: có session nhưng status null (dữ liệu cũ) PHẢI đếm; có status nhưng KHÔNG session
    // (không thể xảy ra ở production, nhưng chính là ca phân biệt được hai định nghĩa) KHÔNG được đếm.
    [Fact]
    public async Task Started_DemTheoSessionId_KhongTheoInterviewStatus()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org);
        // Số đếm CỐ Ý bất đối xứng (2 session-không-status vs 1 status-không-session): fixture "1 và 1" cho
        // hai định nghĩa ra cùng con số ⇒ mutation vẫn xanh (bẫy seed-trùng 2026-08-13, tự dính lượt đầu).
        SeedMembership(t.Db, c.Id, sessionId: Guid.NewGuid(), interviewStatus: null);
        SeedMembership(t.Db, c.Id, sessionId: Guid.NewGuid(), interviewStatus: null);
        SeedMembership(t.Db, c.Id, sessionId: null, interviewStatus: InterviewProgressStatus.InProgress);

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);

        Assert.Equal(3, res.Interviews.Joined);
        Assert.Equal(2, res.Interviews.Started);
        var per = Assert.Single(res.PerCampaign);
        Assert.Equal(2, per.Started);
    }

    [Fact]
    public async Task OrgA_KhongThayOrgB_MoiKhoi()
    {
        using var t = new CampaignTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var a = SeedCampaign(t.Db, orgA, createdAt: T0.AddDays(3));
        var b = SeedCampaign(t.Db, orgB, createdAt: T0.AddDays(3), title: "B");

        // Org B có dữ liệu ở MỌI bảng; org A trống hoàn toàn.
        SeedSubmission(t.Db, b.Id, fit: 90, risk: "Low", skills: new() { "Rust" });
        SeedInvitation(t.Db, b.Id, "b@x.com", emailSentAt: T0.AddDays(4));
        var mb = SeedMembership(t.Db, b.Id, joinedAt: T0.AddDays(5), sessionId: Guid.NewGuid(),
            interviewStatus: InterviewProgressStatus.Completed, interviewStartedAt: T0.AddDays(5));
        SeedRanking(t.Db, b.Id, 88m, updatedAt: T0.AddDays(6), sessionId: mb.SessionId);
        SeedFlag(t.Db, b.Id, "tab_switch");

        var resA = await NewService(t.NewContext()).GetAsync(orgA, Period30d, default);
        Assert.Equal(1, resA.Campaigns.Total);
        Assert.Equal(0, resA.Screening.Submissions);
        Assert.Empty(resA.Screening.TopSkills);
        Assert.Null(resA.Screening.MedianFitScore);
        Assert.Equal(0, resA.Invitations.Total);
        Assert.Equal(0, resA.Interviews.Joined);
        Assert.Equal(0, resA.Interviews.Scored);
        Assert.Empty(resA.Interviews.FlagsBySignal);
        Assert.Null(resA.Interviews.MedianScore);
        // Bucket: chỉ campaign của A được tạo trong kỳ — không có dòng chảy nào của B.
        var bucket = Assert.Single(resA.Buckets);
        Assert.Equal(1, bucket.CampaignsCreated);
        Assert.Equal(0, bucket.InvitationsSent);
        Assert.Equal(0, bucket.Joins);
        Assert.Equal(0, bucket.InterviewsStarted);
        Assert.Equal(0, bucket.Scored);
        Assert.Equal(a.Id, Assert.Single(resA.PerCampaign).CampaignId);

        // Đối chứng dương: chính dữ liệu đó org B thấy đủ (phép đo không rỗng vì lý do khác).
        var resB = await NewService(t.NewContext()).GetAsync(orgB, Period30d, default);
        Assert.Equal(1, resB.Screening.Submissions);
        Assert.Equal("Rust", Assert.Single(resB.Screening.TopSkills).Skill);
        Assert.Equal(1, resB.Invitations.Sent);
        Assert.Equal(1, resB.Interviews.Scored);
        Assert.Equal("tab_switch", Assert.Single(resB.Interviews.FlagsBySignal).SignalType);
        Assert.Equal(b.Id, Assert.Single(resB.PerCampaign).CampaignId);
    }

    // ── bất biến 2: campaign soft-delete không đếm (kể cả bảng con qua query filter DB13) ─────────

    [Fact]
    public async Task CampaignSoftDelete_KhongDem_KeCaBangCon()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var live = SeedCampaign(t.Db, org, createdAt: T0.AddDays(3));
        var dead = SeedCampaign(t.Db, org, createdAt: T0.AddDays(3), title: "dead");
        SeedSubmission(t.Db, dead.Id, fit: 10, skills: new() { "COBOL" });
        SeedInvitation(t.Db, dead.Id, "d@x.com", emailSentAt: T0.AddDays(4));
        SeedMembership(t.Db, dead.Id, joinedAt: T0.AddDays(4), sessionId: Guid.NewGuid());
        SeedRanking(t.Db, dead.Id, 10m, updatedAt: T0.AddDays(5));
        SeedFlag(t.Db, dead.Id, "paste");
        dead.DeletedAt = DateTime.UtcNow;
        t.Db.SaveChanges();

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(1, res.Campaigns.Total);
        Assert.Equal(0, res.Screening.Submissions);
        Assert.Empty(res.Screening.TopSkills);
        Assert.Equal(0, res.Invitations.Total);
        Assert.Equal(0, res.Interviews.Joined);
        Assert.Equal(0, res.Interviews.Scored);
        Assert.Empty(res.Interviews.FlagsBySignal);
        Assert.Equal(live.Id, Assert.Single(res.PerCampaign).CampaignId);
        var bucket = Assert.Single(res.Buckets);
        Assert.Equal(1, bucket.CampaignsCreated);
        Assert.Equal(0, bucket.InvitationsSent + bucket.Joins + bucket.InterviewsStarted + bucket.Scored);
    }

    // ── bất biến 3: Pass/Fail/undetermined == GetCampaignResultsAsync từng dòng ───────────────────

    [Fact]
    public async Task PassFail_KhopByteEqual_VoiGetResults_OverrideThang_RotSanFail_NguongNullUndetermined()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var crit = Criterion("Giao tiếp", minPct: 50);
        var withThreshold = SeedCampaign(t.Db, org, passScorePct: 60, criteria: crit);
        var noThreshold = SeedCampaign(t.Db, org, passScorePct: null, title: "no-threshold");

        // withThreshold: override "Pass" dù điểm thấp · rớt sàn dù tổng cao · pass thường · fail thường ·
        // override "Fail" dù điểm cao.
        SeedRanking(t.Db, withThreshold.Id, 10m, overrideResult: "Pass");
        SeedRanking(t.Db, withThreshold.Id, 95m, snapshot: Snap("Giao tiếp", 40m, crit.Id));
        SeedRanking(t.Db, withThreshold.Id, 70m, snapshot: Snap("Giao tiếp", 80m, crit.Id));
        SeedRanking(t.Db, withThreshold.Id, 59.99m);
        SeedRanking(t.Db, withThreshold.Id, 99m, overrideResult: "Fail");
        // override SCORE (không override result) đẩy lên qua ngưỡng ⇒ Pass theo điểm effective.
        SeedRanking(t.Db, withThreshold.Id, 20m, overrideScore: 61m);
        // Thêm một Pass thường để Pass (4) ≠ Fail (3) — fixture đối xứng làm mutation "đếm Fail thay Pass" XANH giả.
        SeedRanking(t.Db, withThreshold.Id, 75m);
        // noThreshold: 2 dòng không kết luận, 1 dòng override.
        SeedRanking(t.Db, noThreshold.Id, 90m);
        SeedRanking(t.Db, noThreshold.Id, 10m);
        SeedRanking(t.Db, noThreshold.Id, 50m, overrideResult: "Pass");

        var svc = NewCampaignService(t.NewContext());
        var rows = (await svc.GetCampaignResultsAsync(org, withThreshold.Id, default)).Results
            .Concat((await svc.GetCampaignResultsAsync(org, noThreshold.Id, default)).Results)
            .ToList();
        var expectedPass = rows.Count(r => r.Result == "Pass");
        var expectedFail = rows.Count(r => r.Result == "Fail");
        var expectedNull = rows.Count(r => r.Result is null);
        // Fixture phải thật sự phân kỳ ở cả 3 nhánh VÀ ba con số đôi một khác nhau — bằng nhau thì
        // hoán đổi nhánh (đếm Fail thay Pass) vẫn cho cùng số, phép so bằng vô nghĩa.
        Assert.Equal(10, rows.Count);
        Assert.Equal((5, 3, 2), (expectedPass, expectedFail, expectedNull));

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(expectedPass, res.Interviews.Passed);
        Assert.Equal(expectedFail, res.Interviews.Failed);
        Assert.Equal(expectedNull, res.Interviews.Undetermined);
        Assert.Equal(rows.Count, res.Interviews.Scored);

        // Từng campaign cũng khớp (perCampaign.passed) — withThreshold Pass 4 / Fail 3, noThreshold Pass 1 / Fail 0.
        var perWith = res.PerCampaign.Single(p => p.CampaignId == withThreshold.Id);
        var perNo = res.PerCampaign.Single(p => p.CampaignId == noThreshold.Id);
        Assert.Equal(4, perWith.Passed);
        Assert.Equal(1, perNo.Passed);
        Assert.Equal((7, 3), (perWith.Scored, perNo.Scored));
        // Median điểm effective (override ?? AI): withThreshold = {10,59.99,61,70,75,95,99} ⇒ 70.
        Assert.Equal(70m, perWith.MedianScore);
    }

    [Fact]
    public async Task RotSan_KhopTheoTen_KhiSnapshotKhongCoCriterionId()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var crit = Criterion("Kỹ thuật", minPct: 60);
        var c = SeedCampaign(t.Db, org, passScorePct: 50, criteria: crit);
        SeedRanking(t.Db, c.Id, 90m, snapshot: Snap("kỹ thuật ", 30m, critId: null));   // khớp tên, Trim + ignore-case

        var rows = (await NewCampaignService(t.NewContext()).GetCampaignResultsAsync(org, c.Id, default)).Results;
        Assert.Equal("Fail", Assert.Single(rows).Result);

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(0, res.Interviews.Passed);
        Assert.Equal(1, res.Interviews.Failed);
    }

    // ── bất biến 4: invitations.* == GetInvitationsAsync (cùng ResolveDeliveryStatus + cùng phép ghép) ──

    [Fact]
    public async Task Invitations_KhopVoiGetInvitations_Du5TrangThai_TotalBangTong()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org);
        var past = DateTime.UtcNow.AddDays(-1);

        var queued = SeedInvitation(t.Db, c.Id, "queued@x.com");
        var sent = SeedInvitation(t.Db, c.Id, "sent@x.com", emailSentAt: T0.AddDays(1));
        var expired = SeedInvitation(t.Db, c.Id, "expired@x.com", emailSentAt: T0.AddDays(1), expiresAt: past);
        var revoked = SeedInvitation(t.Db, c.Id, "revoked@x.com", emailSentAt: T0.AddDays(1), revokedAt: T0.AddDays(2));
        // Joined qua QUAN HỆ THẬT invitation_id (FX1) — dù đã hết hạn, Joined vẫn thắng Expired.
        var joinedByLink = SeedInvitation(t.Db, c.Id, "joined@x.com", emailSentAt: T0.AddDays(1), expiresAt: past);
        SeedMembership(t.Db, c.Id, invitationId: joinedByLink.Id, email: "joined@x.com");
        // Joined qua FALLBACK email (membership lịch sử không có invitation_id).
        var joinedLegacy = SeedInvitation(t.Db, c.Id, "Legacy@X.com", emailSentAt: T0.AddDays(1));
        SeedMembership(t.Db, c.Id, invitationId: null, email: "legacy@x.com");
        // Revoked THẮNG Joined: lời mời cũ cùng email đã revoke không "thơm lây" (D4).
        var revokedButJoined = SeedInvitation(t.Db, c.Id, "joined@x.com", emailSentAt: T0.AddDays(1), revokedAt: T0.AddDays(2));

        var page = await NewCampaignService(t.NewContext()).GetInvitationsAsync(org, c.Id, null, null, null, null, default);
        Assert.Equal(7, page.Items.Count);
        int Count(string s) => page.Items.Count(i => i.Status == s);

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(page.Items.Count, res.Invitations.Total);
        Assert.Equal(Count(InvitationDeliveryStatus.Queued), res.Invitations.Queued);
        Assert.Equal(Count(InvitationDeliveryStatus.Sent), res.Invitations.Sent);
        Assert.Equal(Count(InvitationDeliveryStatus.Joined), res.Invitations.Joined);
        Assert.Equal(Count(InvitationDeliveryStatus.Expired), res.Invitations.Expired);
        Assert.Equal(Count(InvitationDeliveryStatus.Revoked), res.Invitations.Revoked);
        Assert.Equal(res.Invitations.Total,
            res.Invitations.Queued + res.Invitations.Sent + res.Invitations.Joined + res.Invitations.Expired + res.Invitations.Revoked);

        // Fixture phải phân kỳ đủ 5 nhánh — nếu không, so bằng không chứng minh gì.
        Assert.Equal(1, res.Invitations.Queued);
        Assert.Equal(1, res.Invitations.Sent);
        Assert.Equal(2, res.Invitations.Joined);
        Assert.Equal(1, res.Invitations.Expired);
        Assert.Equal(2, res.Invitations.Revoked);
        Assert.Equal(7, Assert.Single(res.PerCampaign).Invited);
    }

    // ── bất biến 5: bucket neo đúng mốc thời gian ─────────────────────────────────────────────────

    [Fact]
    public async Task Bucket_InvitationsSent_TheoEmailSentAt_KhongPhaiCreatedAt()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org, createdAt: T0.AddDays(-10));   // ngoài kỳ ⇒ campaignsCreated = 0

        // created_at TRONG kỳ nhưng email_sent_at NGOÀI kỳ ⇒ KHÔNG tính.
        SeedInvitation(t.Db, c.Id, "a@x.com", createdAt: T0.AddDays(2), emailSentAt: T0.AddDays(40));
        // created_at NGOÀI kỳ nhưng email_sent_at TRONG kỳ ⇒ tính (đúng ngày 5).
        SeedInvitation(t.Db, c.Id, "b@x.com", createdAt: T0.AddDays(-5), emailSentAt: T0.AddDays(5).AddHours(3));
        // Chưa gửi (Queued) ⇒ không tính.
        SeedInvitation(t.Db, c.Id, "c@x.com", createdAt: T0.AddDays(2));

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        var bucket = Assert.Single(res.Buckets);
        Assert.Equal(T0.AddDays(5), bucket.PeriodStart);
        Assert.Equal(1, bucket.InvitationsSent);
        Assert.Equal(0, bucket.CampaignsCreated);
        // Khối stock vẫn đếm cả 3 (không lọc theo kỳ).
        Assert.Equal(3, res.Invitations.Total);
    }

    [Fact]
    public async Task Bucket_MoiCotNeoDungMoc_SapTangTheoPeriodStart_MonthGop()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org, createdAt: T0.AddDays(20));
        // joins theo joined_at (ngày 3); interviewsStarted theo interview_started_at (ngày 7) — CÙNG membership,
        // hai mốc khác ngày ⇒ hai bucket khác nhau; created_at của membership nằm ngoài kỳ.
        var m = SeedMembership(t.Db, c.Id, joinedAt: T0.AddDays(3), sessionId: Guid.NewGuid(),
            interviewStatus: InterviewProgressStatus.Completed, interviewStartedAt: T0.AddDays(7));
        // scored theo ranking.updated_at (ngày 1) — thêm KHÔNG theo thứ tự thời gian để phép sắp có việc.
        SeedRanking(t.Db, c.Id, 80m, updatedAt: T0.AddDays(1), sessionId: m.SessionId);

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(new[] { T0.AddDays(1), T0.AddDays(3), T0.AddDays(7), T0.AddDays(20) }, res.Buckets.Select(b => b.PeriodStart));
        Assert.Equal(1, res.Buckets[0].Scored);
        Assert.Equal(1, res.Buckets[1].Joins);
        Assert.Equal(1, res.Buckets[2].InterviewsStarted);
        Assert.Equal(1, res.Buckets[3].CampaignsCreated);

        // groupBy=month gộp cả 4 vào 1 bucket đầu tháng.
        var monthly = await NewService(t.NewContext()).GetAsync(org,
            new AnalyticsPeriodResult(T0, T0.AddDays(30), AnalyticsGranularity.Month), default);
        var mb = Assert.Single(monthly.Buckets);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), mb.PeriodStart);
        Assert.Equal((1, 1, 1, 1), (mb.Scored, mb.Joins, mb.InterviewsStarted, mb.CampaignsCreated));
        Assert.Equal("month", monthly.Granularity);
    }

    // ── interviews: pendingScore / inProgress / completed / started đúng định nghĩa ───────────────

    [Fact]
    public async Task Interviews_PendingScore_LaCompletedCoSessionMaChuaCoRanking()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org);
        var scoredSession = Guid.NewGuid();
        SeedMembership(t.Db, c.Id, sessionId: scoredSession, interviewStatus: InterviewProgressStatus.Completed);   // đã có ranking
        SeedMembership(t.Db, c.Id, sessionId: Guid.NewGuid(), interviewStatus: InterviewProgressStatus.Completed);  // pending
        SeedMembership(t.Db, c.Id, sessionId: Guid.NewGuid(), interviewStatus: InterviewProgressStatus.InProgress); // đang thi
        SeedMembership(t.Db, c.Id, sessionId: null, interviewStatus: null);                                          // chưa start
        SeedRanking(t.Db, c.Id, 70m, sessionId: scoredSession);

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(4, res.Interviews.Joined);
        Assert.Equal(3, res.Interviews.Started);
        Assert.Equal(1, res.Interviews.InProgress);
        Assert.Equal(2, res.Interviews.Completed);
        Assert.Equal(1, res.Interviews.Scored);
        Assert.Equal(1, res.Interviews.PendingScore);
        var per = Assert.Single(res.PerCampaign);
        Assert.Equal((4, 3, 1), (per.Joined, per.Started, per.Scored));
    }

    // ── screening: analyzed / byStatus / topSkills / risk ────────────────────────────────────────

    [Fact]
    public async Task Screening_AnalyzedGomInvited_ByStatusEnumString_RiskDemDungNhan()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org);
        SeedSubmission(t.Db, c.Id, CvSubmissionStatus.Analyzed, fit: 80, risk: "Low");
        SeedSubmission(t.Db, c.Id, CvSubmissionStatus.Invited, fit: 40, risk: "medium");   // hoa/thường khác vẫn đếm
        SeedSubmission(t.Db, c.Id, CvSubmissionStatus.Rejected, fit: null, risk: null);
        SeedSubmission(t.Db, c.Id, CvSubmissionStatus.Pending, fit: null, risk: "weird");   // nhãn lạ không vào 3 nhóm

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(4, res.Screening.Submissions);
        Assert.Equal(2, res.Screening.Analyzed);
        Assert.Equal(new[] { "Pending", "Rejected", "Analyzed", "Invited" }, res.Screening.ByStatus.Select(s => s.Status));
        Assert.Equal(new[] { 1, 1, 0 }, res.Screening.RiskBySeverity.Select(r => r.Count));
        Assert.Equal(60m, res.Screening.MedianFitScore);   // (40+80)/2 — bỏ null
    }

    [Fact]
    public async Task TopSkills_KhongPhanBietHoaThuong_HienDangGapDauTien_Top10_HoaTheoTen()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org);
        SeedSubmission(t.Db, c.Id, skills: new() { "SQL", " sql ", "Java" });      // "sql" trùng trong CÙNG CV ⇒ đếm 1
        SeedSubmission(t.Db, c.Id, skills: new() { "sql", "java", "Go", "" });
        SeedSubmission(t.Db, c.Id, skills: new() { "Sql", "Ada" });
        SeedSubmission(t.Db, c.Id, skills: null);
        for (var i = 0; i < 12; i++)
            SeedSubmission(t.Db, c.Id, skills: new() { $"z{i:00}" });

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(10, res.Screening.TopSkills.Count);
        Assert.Equal(("SQL", 3), (res.Screening.TopSkills[0].Skill, res.Screening.TopSkills[0].Count));
        Assert.Equal(("Java", 2), (res.Screening.TopSkills[1].Skill, res.Screening.TopSkills[1].Count));
        // Hoà count=1: Ada < Go < z00 … (A→Z) — 10 chỗ còn 8 cho nhóm 1.
        Assert.Equal(new[] { "Ada", "Go", "z00", "z01", "z02", "z03", "z04", "z05" }, res.Screening.TopSkills.Skip(2).Select(s => s.Skill));
    }

    // ── flagsBySignal sắp giảm theo count, hoà theo tên ───────────────────────────────────────────

    [Fact]
    public async Task FlagsBySignal_GiamTheoCount_HoaTheoTen()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org);
        SeedFlag(t.Db, c.Id, "paste");
        SeedFlag(t.Db, c.Id, "tab_switch");
        SeedFlag(t.Db, c.Id, "tab_switch");
        SeedFlag(t.Db, c.Id, "focus_lost");

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(new[] { ("tab_switch", 2), ("focus_lost", 1), ("paste", 1) },
            res.Interviews.FlagsBySignal.Select(f => (f.SignalType, f.Count)));
    }

    // ── band / median thuần ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(19.99, 0)]
    [InlineData(20, 1)]
    [InlineData(39.99, 1)]
    [InlineData(40, 2)]
    [InlineData(60, 3)]
    [InlineData(79.99, 3)]
    [InlineData(80, 4)]
    [InlineData(100, 4)]
    public void Band_BienDuoiBaoGom_100RoiVaoBandCuoi(decimal score, int expectedIndex)
        => Assert.Equal(expectedIndex, CampaignAnalyticsService.BandIndex(score));

    [Fact]
    public void Median_Le_Chan_LamTron2ChuSo_RongNull()
    {
        Assert.Null(CampaignAnalyticsService.Median(Array.Empty<decimal>()));
        Assert.Equal(5m, CampaignAnalyticsService.Median(new[] { 9m, 1m, 5m }));
        Assert.Equal(3.5m, CampaignAnalyticsService.Median(new[] { 4m, 1m, 3m, 6m }));
        Assert.Equal(55.26m, CampaignAnalyticsService.Median(new[] { 55.25m, 55.26m }));   // 55.255 → 55.26
    }

    // ── hợp đồng JSON: tên khoá camelCase đúng như FE bind ───────────────────────────────────────

    [Fact]
    public async Task Json_TenKhoaCamelCase_DungHopDong()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var c = SeedCampaign(t.Db, org, createdAt: T0.AddDays(2), title: "ASCII-TITLE-SENTINEL");
        SeedSubmission(t.Db, c.Id, fit: 70, risk: "Low", skills: new() { "SQL" });
        var inv = SeedInvitation(t.Db, c.Id, "j@x.com", emailSentAt: T0.AddDays(3));
        var m = SeedMembership(t.Db, c.Id, joinedAt: T0.AddDays(4), sessionId: Guid.NewGuid(),
            interviewStatus: InterviewProgressStatus.Completed, interviewStartedAt: T0.AddDays(4), invitationId: inv.Id);
        SeedRanking(t.Db, c.Id, 66m, updatedAt: T0.AddDays(5), sessionId: m.SessionId);
        SeedFlag(t.Db, c.Id, "tab_switch");

        var result = await NewController(t.Db, org).Analytics(from: T0, to: T0.AddDays(30), groupBy: "day");
        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result.Result).Value, Web);

        foreach (var key in new[]
                 {
                     "\"from\":", "\"to\":", "\"granularity\":\"day\"",
                     "\"campaigns\":{", "\"total\":", "\"byStatus\":[", "\"status\":\"Active\"", "\"count\":",
                     "\"screening\":{", "\"submissions\":", "\"analyzed\":", "\"medianFitScore\":70",
                     "\"fitDistribution\":[", "\"band\":\"0-19\"", "\"band\":\"80-100\"",
                     "\"riskBySeverity\":[", "\"risk\":\"Low\"", "\"risk\":\"Medium\"", "\"risk\":\"High\"",
                     "\"topSkills\":[", "\"skill\":\"SQL\"",
                     "\"invitations\":{", "\"queued\":", "\"sent\":", "\"joined\":", "\"expired\":", "\"revoked\":",
                     "\"interviews\":{", "\"started\":", "\"inProgress\":", "\"completed\":", "\"scored\":",
                     "\"pendingScore\":", "\"passed\":", "\"failed\":", "\"undetermined\":", "\"medianScore\":66",
                     "\"scoreDistribution\":[", "\"flagsBySignal\":[", "\"signalType\":\"tab_switch\"",
                     "\"buckets\":[", "\"periodStart\":", "\"campaignsCreated\":", "\"invitationsSent\":",
                     "\"joins\":", "\"interviewsStarted\":",
                     "\"perCampaign\":[", "\"campaignId\":", "\"title\":\"ASCII-TITLE-SENTINEL\"", "\"createdAt\":",
                     "\"invited\":",
                 })
            Assert.Contains(key, json);

        // Không lộ tên PascalCase (serializer sai) và không có IEnumerable lazy ném lúc serialize.
        Assert.DoesNotContain("\"Campaigns\":", json);
        Assert.DoesNotContain("\"PerCampaign\":", json);
    }

    // ── hiệu năng: số truy vấn CỐ ĐỊNH (không N+1) và lọc org/campaign nằm TRONG SQL ──────────────

    [Fact]
    public async Task SoTruyVanCoDinh6_LocOrgVaCampaignTrongSql_KhongTangTheoSoCampaign()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();

        int QueriesFor(int campaigns, out List<string> sql)
        {
            var spy = new SqlSpy();
            for (var i = 0; i < campaigns; i++)
            {
                var c = SeedCampaign(t.Db, org, criteria: Criterion($"C{i}", minPct: 50));
                SeedSubmission(t.Db, c.Id, fit: 50, skills: new() { "SQL" });
                SeedInvitation(t.Db, c.Id, $"u{i}-{Guid.NewGuid():N}@x.com", emailSentAt: T0.AddDays(1));
                var m = SeedMembership(t.Db, c.Id, sessionId: Guid.NewGuid(), interviewStatus: InterviewProgressStatus.Completed);
                SeedRanking(t.Db, c.Id, 70m, sessionId: m.SessionId, snapshot: Snap($"C{i}", 60m, null));
                SeedFlag(t.Db, c.Id, "paste");
            }
            NewService(t.NewContext(spy)).GetAsync(org, Period30d, default).GetAwaiter().GetResult();
            sql = spy.Commands;
            return spy.Commands.Count;
        }

        var n1 = QueriesFor(1, out var sql1);
        var n4 = QueriesFor(4, out _);
        Assert.Equal(n1, n4);   // không N+1 theo số campaign
        Assert.Equal(6, n1);    // campaigns(+criteria) · submissions · invitations · memberships · rankings · flags

        // Lọc phải nằm trong SQL: câu đầu lọc org_id; 5 câu sau lọc campaign_id theo danh sách.
        Assert.Contains("org_id", sql1[0]);
        Assert.All(sql1.Skip(1), q => Assert.Contains("campaign_id", q));
        // Không nạp nguyên entity: cột không dùng (cv_parsed_text, token_hash, note) không được xuất hiện.
        Assert.DoesNotContain("cv_parsed_text", string.Join("\n", sql1));
        Assert.DoesNotContain("token_hash", string.Join("\n", sql1));
    }

    private sealed class SqlSpy : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    // ── perCampaign: mọi campaign của org, mới nhất trước, cột cùng định nghĩa ───────────────────

    [Fact]
    public async Task PerCampaign_MoiNhatTruoc_MedianTheoCampaign_CampaignTrongVanCo()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var older = SeedCampaign(t.Db, org, CampaignStatus.Closed, createdAt: T0.AddDays(1), title: "older");
        var newer = SeedCampaign(t.Db, org, CampaignStatus.Draft, createdAt: T0.AddDays(9), title: "newer");
        SeedRanking(t.Db, older.Id, 30m);
        SeedRanking(t.Db, older.Id, 50m, overrideScore: 90m);   // effective 90 ⇒ median (50? không) = (30+90)/2 = 60

        var res = await NewService(t.NewContext()).GetAsync(org, Period30d, default);
        Assert.Equal(new[] { "newer", "older" }, res.PerCampaign.Select(p => p.Title));
        Assert.Equal(new[] { "Draft", "Closed" }, res.PerCampaign.Select(p => p.Status));
        Assert.Null(res.PerCampaign[0].MedianScore);
        Assert.Equal(60m, res.PerCampaign[1].MedianScore);
        Assert.Equal(2, res.PerCampaign[1].Scored);
        Assert.Equal(2, res.Campaigns.Total);
        Assert.Equal(new[] { ("Draft", 1), ("Closed", 1) }, res.Campaigns.ByStatus.Select(s => (s.Status, s.Count)));
    }
}
