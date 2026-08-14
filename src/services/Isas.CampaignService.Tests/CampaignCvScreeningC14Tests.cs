using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Sàng CV async — vai HR technical screener (đối chiếu nhu cầu công việc + callback + shortlist
/// + PATCH); 0 credit.
/// (a) Filtered → publish cv_screening_queue + Analyzing;
/// (b) callback cv-result → strengths/gaps + jobFitScore TÍNH TỪ BẰNG CHỨNG + Analyzed;
/// (c) cv-failed → AnalysisFailed;
/// (d) callback 2 lần → không nhân đôi;
/// (e) callback sau Invited → bỏ qua (không lật);
/// (f) ?sort=score → DESC (null xuống cuối);
/// (g) PATCH email → audit_logs; đã Invited → InvalidOperationException (409).
///
/// ⚠ Thước đo là <c>campaigns.job_needs</c>, KHÔNG còn là <c>campaign_criteria</c> (rubric buổi
/// phỏng vấn — CV là giấy nên model chỉ đoán được). Publisher mock (không cần broker); SQLite
/// in-mem (CampaignTestDb).
/// </summary>
public class CampaignCvScreeningC14Tests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:CallbackBase"] = "http://campaign:8080" })
            .Build();

    private static CvScreeningService NewService(CampaignDbContext db, ICvScreeningPublisher? publisher = null) =>
        new(db, publisher ?? Mock.Of<ICvScreeningPublisher>(), Config(),
            Mock.Of<ILogger<CvScreeningService>>());

    private static Campaign SeedActiveCampaign(CampaignTestDb tdb, Guid owner, string? domain = "BE")
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.Domain = domain;
        camp.JDText = "JD: cần Backend .NET";
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    /// <summary>Chốt bộ nhu cầu công việc cho campaign — thước đo dùng chung cho MỌI ứng viên.</summary>
    private static List<JobNeed> SeedJobNeeds(CampaignTestDb tdb, Guid campaignId, int count = 2)
    {
        var list = Enumerable.Range(0, count).Select(i => new JobNeed
        {
            NeedId = $"need-{i}",
            Category = i % 2 == 0 ? JobNeedCategories.Technical : JobNeedCategories.Communication,
            Text = $"Nhu cầu {i}",
            Source = JobNeedSources.AiSuggested,
        }).ToList();

        var camp = tdb.Db.Campaigns.First(c => c.Id == campaignId);
        camp.JobNeeds = list;
        tdb.Db.SaveChanges();
        return list;
    }

    private static NeedAssessmentItem Assess(string needId, string level, string? evidence = "trích từ CV")
        => new() { NeedId = needId, Area = $"vùng {needId}", Level = level, Evidence = evidence };

    private static CvSubmission SeedCandidate(
        CampaignTestDb tdb, Guid campaignId, CvSubmissionStatus status,
        string? email = null, int? overall = null, string? parsedText = "CV text a@x.com")
    {
        var now = DateTime.UtcNow;
        var cand = new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Email = email,
            CvParsedText = parsedText,
            CvFileUrl = $"campaigns/{campaignId}/candidates/x.pdf",
            ParseStatus = CvParseStatus.Done,
            Status = status,
            OverallMatchScore = overall,
            CreatedAt = now,
            UpdatedAt = now
        };
        tdb.Db.CvSubmissions.Add(cand);
        tdb.Db.SaveChanges();
        return cand;
    }

    // (a) Filtered → publish job + chuyển Analyzing + set last_screening_published_at; job mang criteria.
    [Fact]
    public async Task Filtered_publish_va_chuyen_Analyzing()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedJobNeeds(tdb, camp.Id, 2);
        var c1 = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered, email: "a@x.com");
        var c2 = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered, email: "b@x.com");

        var published = new List<CvScreeningJob>();
        var pub = new Mock<ICvScreeningPublisher>();
        pub.Setup(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()))
           .Callback<CvScreeningJob, CancellationToken>((j, _) => published.Add(j))
           .Returns(Task.CompletedTask);

        var svc = NewService(tdb.NewContext(), pub.Object);
        var n = await svc.PublishScreeningJobsAsync(owner, camp.Id, default);

        Assert.Equal(2, n);
        Assert.Equal(2, published.Count);
        Assert.All(published, j =>
        {
            Assert.Equal(2, j.JobNeeds.Count);                 // thước đo = job_needs của campaign
            Assert.Equal("BE", j.JobCategory);
            Assert.Equal("http://campaign:8080", j.CallbackBase);
            Assert.False(string.IsNullOrEmpty(j.CvText));
        });
        Assert.Contains(published, j => j.CandidateId == c1.Id);
        Assert.Contains(published, j => j.CandidateId == c2.Id);

        using var check = tdb.NewContext();
        var rows = await check.CvSubmissions.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.Equal(CvSubmissionStatus.Analyzing, r.Status);
            Assert.NotNull(r.LastScreeningPublishedAt);
        });
    }

    // (a-bis) chỉ publish ứng viên Filtered — Rejected/Analyzed KHÔNG bị đụng.
    [Fact]
    public async Task Publish_chi_Filtered_bo_qua_Rejected()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedJobNeeds(tdb, camp.Id, 1);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered, email: "a@x.com");
        var rejected = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Rejected, email: "b@x.com");

        var svc = NewService(tdb.NewContext());
        var n = await svc.PublishScreeningJobsAsync(owner, camp.Id, default);

        Assert.Equal(1, n);
        using var check = tdb.NewContext();
        Assert.Equal(CvSubmissionStatus.Rejected, (await check.CvSubmissions.FindAsync(rejected.Id))!.Status);
    }

    // (b) cv-result → strengths/gaps + jobFitScore TÍNH TỪ BẰNG CHỨNG + Analyzed; bỏ needId bịa.
    //
    // ⚠ Tiền đề ĐỔI CÓ CHỦ ĐÍCH so với bản C14: test cũ khẳng định `overall_match_score` = con số
    // AI gửi lên (kẹp [0,100]). Chính khẳng định đó KHOÁ ĐÚNG CÁI BUG đang sửa — đo trên prod, bốn
    // CV có bằng chứng giống hệt nhau nhận 70/70/55/55 vì số đó do model phán chứ không tính từ gì.
    // Nay điểm do service tính, nên request cố ý KHÔNG mang điểm nào cả.
    [Fact]
    public async Task Callback_cv_result_ghi_danh_gia_va_tinh_diem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 2);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        var req = new CvResultCallbackRequest
        {
            Skills = new() { "C#", "SQL" },
            YearsExperience = 3.5m,
            FitSummary = "Ứng viên tốt",
            VerificationRisk = VerificationRisks.Low,
            BonusSignals = new() { "CI/CD" },
            VerifyQuestions = new() { "Hỏi về dự án X" },
            Assessments = new()
            {
                Assess(needs[0].NeedId, NeedLevels.Strong),
                Assess(needs[1].NeedId, NeedLevels.Weak, NeedEvidence.NotFound),
                Assess("need-khong-ton-tai", NeedLevels.Strong),   // id AI bịa → bỏ
            }
        };

        var svc = NewService(tdb.NewContext());
        var outcome = await svc.SaveCvResultAsync(cand.Id, req, default);
        Assert.Equal(CvResultOutcome.Analyzed, outcome);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(CvSubmissionStatus.Analyzed, row!.Status);
        // 1 Strong + 1 Weak trên 2 nhu cầu ⇒ (1 + 0)/2 = 50. Id bịa KHÔNG được kéo mẫu số lên 3.
        Assert.Equal(50, row.OverallMatchScore);
        Assert.Equal(ScreeningVersions.JobFitFromEvidence, row.ScreeningVersion);
        Assert.Equal(3.5m, row.YearsExperience);
        Assert.Contains("C#", row.Skills!);
        Assert.Equal(VerificationRisks.Low, row.VerificationRisk);

        Assert.Equal(needs[0].NeedId, Assert.Single(row.Strengths!).NeedId);
        Assert.Equal(needs[1].NeedId, Assert.Single(row.Gaps!).NeedId);
    }

    // (b-bis) 🔴 BẤT BIẾN CỐT LÕI: cùng bộ mức bằng chứng ⇒ CÙNG điểm.
    // Đây đúng thứ prod đang vi phạm (4 CV bằng chứng giống hệt → 70/70/55/55, và ứng viên yếu hơn
    // xếp trên ứng viên mạnh hơn). Chữ nghĩa `area`/`evidence` khác nhau KHÔNG được làm điểm đổi.
    [Fact]
    public async Task Cung_bo_muc_bang_chung_thi_cung_diem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 3);
        var a = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");
        var b = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "b@x.com");

        CvResultCallbackRequest Req(string evidence) => new()
        {
            FitSummary = evidence,
            Assessments = new()
            {
                Assess(needs[0].NeedId, NeedLevels.Strong, evidence),
                Assess(needs[1].NeedId, NeedLevels.Partial, evidence),
                Assess(needs[2].NeedId, NeedLevels.Weak, NeedEvidence.NotFound),
            }
        };

        await NewService(tdb.NewContext()).SaveCvResultAsync(a.Id, Req("dẫn chứng A"), default);
        await NewService(tdb.NewContext()).SaveCvResultAsync(b.Id, Req("một câu hoàn toàn khác"), default);

        using var check = tdb.NewContext();
        var scoreA = (await check.CvSubmissions.FindAsync(a.Id))!.OverallMatchScore;
        var scoreB = (await check.CvSubmissions.FindAsync(b.Id))!.OverallMatchScore;
        Assert.Equal(scoreA, scoreB);
        Assert.Equal(50, scoreA);   // (1 + 0.5 + 0)/3 = 0.5 → 50
    }

    // (b-ter) Mức lạ ⇒ Weak (chưa chứng minh được), KHÔNG phải Partial: mọi hướng khác đều cho
    // không ứng viên một phần điểm mà không ai đọc được bằng chứng nào.
    [Fact]
    public async Task Muc_la_thi_ve_Weak_khong_phai_nua_diem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        await NewService(tdb.NewContext()).SaveCvResultAsync(cand.Id, new CvResultCallbackRequest
        {
            Assessments = new() { Assess(needs[0].NeedId, "Xuất sắc lắm luôn") }
        }, default);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(0, row!.OverallMatchScore);
        Assert.Equal(NeedLevels.Weak, Assert.Single(row.Gaps!).Level);
    }

    // (b-quater) Mức cao mà KHÔNG trích được gì trong CV ⇒ hạ Weak + ghi đúng câu "Không thấy bằng
    // chứng". Một mức Strong không ai kiểm chứng được thì HR không dùng để bảo vệ quyết định được.
    [Fact]
    public async Task Strong_khong_co_bang_chung_thi_ha_ve_Weak()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        await NewService(tdb.NewContext()).SaveCvResultAsync(cand.Id, new CvResultCallbackRequest
        {
            Assessments = new() { Assess(needs[0].NeedId, NeedLevels.Strong, "   ") }
        }, default);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(0, row!.OverallMatchScore);
        var gap = Assert.Single(row.Gaps!);
        Assert.Equal(NeedLevels.Weak, gap.Level);
        Assert.Equal(NeedEvidence.NotFound, gap.Evidence);
    }

    // (b-quinquies) verificationRisk KHÔNG nhập vào điểm — nó là cờ đứng cạnh, không phải một
    // thành phần của con số. Gộp vào là lặp lại đúng sai lầm bản này đang sửa.
    [Fact]
    public async Task VerificationRisk_khong_lam_doi_diem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 2);
        var low = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "low@x.com");
        var high = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "high@x.com");

        CvResultCallbackRequest Req(string risk) => new()
        {
            VerificationRisk = risk,
            Assessments = new()
            {
                Assess(needs[0].NeedId, NeedLevels.Strong),
                Assess(needs[1].NeedId, NeedLevels.Strong),
            }
        };

        await NewService(tdb.NewContext()).SaveCvResultAsync(low.Id, Req(VerificationRisks.Low), default);
        await NewService(tdb.NewContext()).SaveCvResultAsync(high.Id, Req(VerificationRisks.High), default);

        using var check = tdb.NewContext();
        var rowLow = (await check.CvSubmissions.FindAsync(low.Id))!;
        var rowHigh = (await check.CvSubmissions.FindAsync(high.Id))!;
        Assert.Equal(100, rowLow.OverallMatchScore);
        Assert.Equal(100, rowHigh.OverallMatchScore);          // rủi ro cao KHÔNG bị trừ điểm...
        Assert.Equal(VerificationRisks.High, rowHigh.VerificationRisk);   // ...mà hiện thành cờ
    }

    // (b-sexies) verifyQuestions cắt còn 3 — spec nói TỐI ĐA 3; cắt ở đây chứ không chỉ dặn model,
    // vì mọi thứ chỉ dặn bằng lời đều là thứ model được phép bỏ qua.
    [Fact]
    public async Task VerifyQuestions_cat_con_3()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        await NewService(tdb.NewContext()).SaveCvResultAsync(cand.Id, new CvResultCallbackRequest
        {
            VerifyQuestions = new() { "q1", "q2", "q3", "q4", "q5" },
            Assessments = new() { Assess(needs[0].NeedId, NeedLevels.Strong) }
        }, default);

        using var check = tdb.NewContext();
        Assert.Equal(3, (await check.CvSubmissions.FindAsync(cand.Id))!.VerifyQuestions!.Count);
    }

    // (b-septies) Campaign chưa chốt job_needs ⇒ KHÔNG publish job nào (thà đứng im có lý do đọc
    // được còn hơn sàng một ứng viên mà không đối chiếu với gì rồi trưng ra như đã sàng).
    [Fact]
    public async Task Chua_chot_job_needs_thi_khong_sang_duoc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);   // KHÔNG SeedJobNeeds
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered, email: "a@x.com");

        var pub = new Mock<ICvScreeningPublisher>();
        var svc = NewService(tdb.NewContext(), pub.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PublishScreeningJobsAsync(owner, camp.Id, default));
        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (c) cv-failed → AnalysisFailed + reason.
    [Fact]
    public async Task Callback_cv_failed_set_AnalysisFailed()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        var svc = NewService(tdb.NewContext());
        var outcome = await svc.MarkCvFailedAsync(cand.Id, "Gemini timeout vĩnh viễn", default);
        Assert.Equal(CvFailedOutcome.Failed, outcome);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(CvSubmissionStatus.AnalysisFailed, row!.Status);
        Assert.Contains("timeout", row.RejectReason);
    }

    // (c-bis) cv-failed muộn khi đã Analyzed → KHÔNG hạ cấp (no-op).
    [Fact]
    public async Task Callback_cv_failed_khi_da_Analyzed_khong_ha_cap()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "a@x.com", overall: 80);

        var svc = NewService(tdb.NewContext());
        var outcome = await svc.MarkCvFailedAsync(cand.Id, "late fail", default);
        Assert.Equal(CvFailedOutcome.SkippedAnalyzed, outcome);

        using var check = tdb.NewContext();
        Assert.Equal(CvSubmissionStatus.Analyzed, (await check.CvSubmissions.FindAsync(cand.Id))!.Status);
    }

    // (c-ter) cv-result về khi đang AnalysisFailed (worker callback muộn sau timeout) → recover Analyzed.
    [Fact]
    public async Task Callback_cv_result_recover_tu_AnalysisFailed()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.AnalysisFailed, email: "a@x.com");
        cand.RejectReason = "timeout cũ";
        tdb.Db.SaveChanges();

        var req = new CvResultCallbackRequest
        {
            Assessments = new() { Assess(needs[0].NeedId, NeedLevels.Partial) }
        };

        var svc = NewService(tdb.NewContext());
        await svc.SaveCvResultAsync(cand.Id, req, default);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(CvSubmissionStatus.Analyzed, row!.Status);
        Assert.Null(row.RejectReason);   // xoá lý do fail cũ khi recover
    }

    // (d) callback cv-result 2 lần → điểm KHÔNG nhân đôi (idempotent: xoá cũ rồi ghi lại).
    [Fact]
    public async Task Callback_cv_result_hai_lan_khong_nhan_doi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 2);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        CvResultCallbackRequest Req() => new()
        {
            Assessments = new()
            {
                Assess(needs[0].NeedId, NeedLevels.Strong),
                Assess(needs[1].NeedId, NeedLevels.Partial),
            }
        };

        await NewService(tdb.NewContext()).SaveCvResultAsync(cand.Id, Req(), default);
        await NewService(tdb.NewContext()).SaveCvResultAsync(cand.Id, Req(), default);   // callback lần 2

        using var check = tdb.NewContext();
        var row = (await check.CvSubmissions.FindAsync(cand.Id))!;
        Assert.Equal(2, row.Strengths!.Count);          // vẫn 2, không 4 (replace-all, không cộng dồn)
        Assert.Equal(75, row.OverallMatchScore);        // (1 + 0.5)/2 = 0.75 — lần 2 không làm đổi
    }

    // (e) callback cv-result về SAU khi đã Invited → bỏ qua (không ghi điểm, giữ Invited).
    [Fact]
    public async Task Callback_cv_result_sau_Invited_bo_qua()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Invited, email: "a@x.com", overall: 90);

        var req = new CvResultCallbackRequest
        {
            Assessments = new() { Assess(needs[0].NeedId, NeedLevels.Weak, NeedEvidence.NotFound) }
        };

        var svc = NewService(tdb.NewContext());
        var outcome = await svc.SaveCvResultAsync(cand.Id, req, default);
        Assert.Equal(CvResultOutcome.SkippedInvited, outcome);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(CvSubmissionStatus.Invited, row!.Status);      // giữ nguyên
        Assert.Equal(90, row.OverallMatchScore);                 // KHÔNG bị ghi đè
        Assert.Null(row.Strengths);                              // không ghi đánh giá nào
    }

    // (e-bis) candidate không tồn tại → KeyNotFoundException (→404).
    [Fact]
    public async Task Callback_candidate_khong_ton_tai_nem_KeyNotFound()
    {
        using var tdb = new CampaignTestDb();
        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.SaveCvResultAsync(Guid.NewGuid(), new CvResultCallbackRequest(), default));
    }

    // (f) ?sort=score → DESC theo overall_match_score; chưa Analyzed (null) xuống cuối.
    [Fact]
    public async Task Shortlist_sort_score_DESC_null_cuoi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "mid@x.com", overall: 70);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "top@x.com", overall: 90);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "none@x.com", overall: null);

        var svc = NewService(tdb.NewContext());
        var list = (await svc.GetCandidatesAsync(owner, camp.Id, null, null, null, "score", null, null, null, default)).Items;

        Assert.Equal(3, list.Count);
        Assert.Equal(90, list[0].OverallMatchScore);
        Assert.Equal(70, list[1].OverallMatchScore);
        Assert.Null(list[2].OverallMatchScore);     // null xuống cuối
    }

    // (f-bis) filter minScore + status; ngoài org → 404.
    [Fact]
    public async Task Shortlist_filter_minScore_va_ngoai_org_404()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "a@x.com", overall: 50);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "b@x.com", overall: 85);

        var svc = NewService(tdb.NewContext());
        var filtered = (await svc.GetCandidatesAsync(owner, camp.Id, null, 70, null, "score", null, null, null, default)).Items;
        Assert.Single(filtered);
        Assert.Equal(85, filtered[0].OverallMatchScore);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.GetCandidatesAsync(Guid.NewGuid() /* org khác */, camp.Id, null, null, null, "score", null, null, null, default));
    }

    // (g) PATCH email → cập nhật + audit_logs có row EditCandidate.
    [Fact]
    public async Task Patch_email_cap_nhat_va_ghi_audit()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: null);

        var svc = NewService(tdb.NewContext());
        await svc.PatchCandidateAsync(owner, owner, camp.Id, cand.Id,
            new PatchCandidateRequest { Email = "New@X.com", FullName = "  Nguyễn Văn A  " }, default);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal("new@x.com", row!.Email);        // chuẩn hoá lowercase
        Assert.Equal("Nguyễn Văn A", row.FullName);   // trim
        Assert.True(await check.AuditLogs.AnyAsync(a =>
            a.Action == AuditAction.EditCandidate && a.EntityId == cand.Id));
    }

    // (g-bis) PATCH sau khi đã Invited → InvalidOperationException (→409).
    [Fact]
    public async Task Patch_sau_Invited_nem_InvalidOperationException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Invited, email: "a@x.com");

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PatchCandidateAsync(owner, owner, camp.Id, cand.Id,
                new PatchCandidateRequest { FullName = "X" }, default));
    }

    // ── BK28 — AI điền `full_name` (trước đó NULL 100%: AIService không hề có khái niệm tên) ──────
    //
    // Bất biến: **AI CHỈ ĐIỀN CHỖ TRỐNG, KHÔNG BAO GIỜ ghi đè người.** `StuckScreeningRepublisher`
    // đẩy lại job cho ứng viên kẹt `Analyzing` nên cv-result tới NHIỀU LẦN — gán thẳng `=` sẽ xoá
    // đúng cái tên HR vừa sửa tay qua PATCH ở lần callback kế tiếp.

    private static CvResultCallbackRequest ResultWithName(string? fullName, string needId) => new()
    {
        FullName = fullName,
        Assessments = new() { Assess(needId, NeedLevels.Strong) }
    };

    // (h) cv-result mang fullName → điền vào ô đang trống.
    [Fact]
    public async Task Bk28_callback_co_fullName_thi_dien_vao_o_trong()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");
        Assert.Null(cand.FullName);   // C13 luôn ghi null — đây chính là trạng thái BK28 sinh ra để sửa

        await NewService(tdb.NewContext())
            .SaveCvResultAsync(cand.Id, ResultWithName("  Nguyễn Văn A  ", needs[0].NeedId), default);

        using var check = tdb.NewContext();
        Assert.Equal("Nguyễn Văn A", (await check.CvSubmissions.FindAsync(cand.Id))!.FullName);   // trim
    }

    // (h-bis) 🔴 callback lần 2 KHÔNG được ghi đè tên HR đã sửa tay — ca mà republisher tạo ra thật.
    [Fact]
    public async Task Bk28_callback_lan_2_KHONG_ghi_de_ten_HR_da_PATCH()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        // Lần 1: AI điền tên (đọc nhầm từ CV scan).
        await NewService(tdb.NewContext())
            .SaveCvResultAsync(cand.Id, ResultWithName("Nguyen Van A (OCR sai)", needs[0].NeedId), default);

        // HR sửa tay.
        await NewService(tdb.NewContext()).PatchCandidateAsync(owner, owner, camp.Id, cand.Id,
            new PatchCandidateRequest { FullName = "Nguyễn Văn A" }, default);

        // Lần 2: republisher đẩy lại job → cv-result về lần nữa với tên AI cũ.
        await NewService(tdb.NewContext())
            .SaveCvResultAsync(cand.Id, ResultWithName("Nguyen Van A (OCR sai)", needs[0].NeedId), default);

        using var check = tdb.NewContext();
        Assert.Equal("Nguyễn Văn A", (await check.CvSubmissions.FindAsync(cand.Id))!.FullName);
    }

    // (h-ter) fullName null (CV không có tên rõ ràng) → giữ nguyên giá trị đang có, KHÔNG xoá.
    [Fact]
    public async Task Bk28_fullName_null_thi_giu_nguyen_gia_tri_cu()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");
        cand.FullName = "Tên HR nhập";
        tdb.Db.SaveChanges();

        await NewService(tdb.NewContext())
            .SaveCvResultAsync(cand.Id, ResultWithName(null, needs[0].NeedId), default);

        using var check = tdb.NewContext();
        Assert.Equal("Tên HR nhập", (await check.CvSubmissions.FindAsync(cand.Id))!.FullName);
    }

    // (h-quater) fullName toàn khoảng trắng → coi như KHÔNG có tên (đừng lưu "" làm ô trông như đã điền).
    [Fact]
    public async Task Bk28_fullName_toan_khoang_trang_thi_khong_ghi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        await NewService(tdb.NewContext())
            .SaveCvResultAsync(cand.Id, ResultWithName("   ", needs[0].NeedId), default);

        using var check = tdb.NewContext();
        Assert.Null((await check.CvSubmissions.FindAsync(cand.Id))!.FullName);
    }

    // (h-quinquies) tên vượt varchar(255) → CẮT, không ném. Tràn thì Postgres ném lúc SaveChanges
    // → callback 500 → worker nack → vòng republish (SQLite không enforce độ dài nên chỉ assert
    // được độ dài đã cắt — chính là lý do phải cắt trong CODE chứ không trông vào DB).
    [Fact]
    public async Task Bk28_ten_qua_dai_thi_cat_255_khong_nem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var needs = SeedJobNeeds(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        await NewService(tdb.NewContext())
            .SaveCvResultAsync(cand.Id, ResultWithName(new string('Ạ', 400), needs[0].NeedId), default);

        using var check = tdb.NewContext();
        Assert.Equal(255, (await check.CvSubmissions.FindAsync(cand.Id))!.FullName!.Length);
    }

    // (g-ter) PATCH email trùng ứng viên khác trong campaign → ArgumentException (→400).
    [Fact]
    public async Task Patch_email_trung_nem_ArgumentException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "taken@x.com");
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered, email: null);

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.PatchCandidateAsync(owner, owner, camp.Id, cand.Id,
                new PatchCandidateRequest { Email = "taken@x.com" }, default));
    }
}
