using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// RNK1 · HĐ-6 — điều kiện LOẠI ở vòng sàng CV. <c>job_needs[].isMustHave</c> (HR sở hữu, AI không
/// đề xuất); thiếu bằng chứng Strong/Partial cho BẤT KỲ must-have ⇒ ứng viên KHÔNG đủ điều kiện
/// (<c>eligible = false</c>) ngay lúc sàng, tính READ-TIME (không cột). <c>must_have_*</c> nay đếm
/// CHỈ nhu cầu <c>isMustHave</c> — một nguồn tính duy nhất <see cref="CvMustHaveEvaluator"/>.
/// </summary>
public class CvMustHaveRnk1B5Tests
{
    private static CampaignSvc NewCampaignService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static CvScreeningService NewScreeningService(CampaignDbContext db) =>
        new(db, Mock.Of<ICvScreeningPublisher>(), Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<CvScreeningService>>());

    private static JobNeed Need(string id, bool mustHave) => new()
    {
        NeedId = id, Category = JobNeedCategories.Technical, Text = $"Nhu cầu {id}",
        Source = JobNeedSources.HrEdited, IsMustHave = mustHave,
    };

    private static NeedAssessment Assess(string needId, string level) => new()
    {
        NeedId = needId, Area = $"vùng {needId}", Level = level, Evidence = "trích từ CV",
    };

    private static Campaign SeedCampaign(
        CampaignTestDb tdb, Guid org, List<JobNeed>? needs = null,
        CampaignStatus status = CampaignStatus.Active)
    {
        var c = CampaignTestDb.NewCampaign(org, status);
        c.Domain = "BE";
        c.JDText = "JD";
        c.JobNeeds = needs;
        tdb.Db.Campaigns.Add(c);
        tdb.Db.SaveChanges();
        return c;
    }

    private static CvSubmission SeedCandidate(
        CampaignTestDb tdb, Guid campaignId, string email,
        List<NeedAssessment>? strengths = null, List<NeedAssessment>? gaps = null,
        int? overallMatchScore = 80, CvSubmissionStatus status = CvSubmissionStatus.Analyzed)
    {
        var now = DateTime.UtcNow;
        var cand = new CvSubmission
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Email = email,
            CvParsedText = "CV", CvFileUrl = $"campaigns/{campaignId}/candidates/x.pdf",
            ParseStatus = CvParseStatus.Done, Status = status,
            OverallMatchScore = overallMatchScore,
            Strengths = strengths, Gaps = gaps,
            CreatedAt = now, UpdatedAt = now,
        };
        tdb.Db.CvSubmissions.Add(cand);
        tdb.Db.SaveChanges();
        return cand;
    }

    // ── 1. jsonb vắng khoá isMustHave ⇒ false (KHÔNG migration) ──────────────────────────────────
    [Fact]
    public void JobNeed_JsonbVangIsMustHave_Deserialize_False()
    {
        // Row trước RNK1 — JSON không có khoá IsMustHave. Đúng converter mà CampaignDbContext dùng.
        var need = JsonSerializer.Deserialize<List<JobNeed>>(
            """[{"NeedId":"n1","Category":"Technical","Text":"x","Source":"HrEdited"}]""")!.Single();

        Assert.False(need.IsMustHave);
    }

    // ── 2. 0 must-have ⇒ Eligible=true, Total=0, Missing rỗng (HR chưa khai điều kiện loại) ────────
    [Fact]
    public void Evaluate_KhongCoMustHave_EligibleTrue_Total0()
    {
        var r = CvMustHaveEvaluator.Evaluate(
            new[] { Need("n0", mustHave: false), Need("n1", mustHave: false) },
            strengths: Array.Empty<NeedAssessment>(),
            gaps: new[] { Assess("n0", NeedLevels.Weak) });

        Assert.True(r.Eligible);
        Assert.Equal(0, r.MustHaveTotal);
        Assert.Equal(0, r.MustHaveMet);
        Assert.Empty(r.Missing);
    }

    // ── 3. 2 must-have, 1 Weak ⇒ Eligible=false, Missing 1, Met 1/2 ───────────────────────────────
    [Fact]
    public void Evaluate_2MustHave_1Weak_Ineligible_Missing1_Met1()
    {
        var r = CvMustHaveEvaluator.Evaluate(
            new[] { Need("n0", true), Need("n1", true) },
            strengths: new[] { Assess("n0", NeedLevels.Strong) },
            gaps: new[] { Assess("n1", NeedLevels.Weak) });

        Assert.False(r.Eligible);
        Assert.Equal(2, r.MustHaveTotal);
        Assert.Equal(1, r.MustHaveMet);
        Assert.Equal("n1", Assert.Single(r.Missing).NeedId);
    }

    // ── 4. must-have KHÔNG có assessment nào ⇒ nằm trong Missing (phân biệt "chưa đánh giá") ───────
    [Fact]
    public void Evaluate_MustHave_KhongCoAssessment_Missing_Ineligible()
    {
        var r = CvMustHaveEvaluator.Evaluate(
            new[] { Need("n0", true), Need("n1", true) },
            strengths: new[] { Assess("n0", NeedLevels.Strong) },
            gaps: Array.Empty<NeedAssessment>());   // n1 không được đánh giá

        Assert.False(r.Eligible);
        Assert.Equal("n1", Assert.Single(r.Missing).NeedId);
        Assert.Equal(1, r.MustHaveMet);
    }

    // ── 5. must-have Partial ⇒ tính Met (Partial cũng là bằng chứng) ──────────────────────────────
    [Fact]
    public void Evaluate_MustHave_Partial_TinhMet()
    {
        var r = CvMustHaveEvaluator.Evaluate(
            new[] { Need("n0", true) },
            strengths: new[] { Assess("n0", NeedLevels.Partial) },
            gaps: Array.Empty<NeedAssessment>());

        Assert.True(r.Eligible);
        Assert.Equal(1, r.MustHaveMet);
        Assert.Empty(r.Missing);
    }

    // ── 6. InviteShortlisted: ineligible ⇒ failed[] lý do, KHÔNG tạo invitation ───────────────────
    [Fact]
    public async Task InviteShortlisted_Ineligible_VaoFailed_KhongTaoInvitation()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org, needs: new List<JobNeed> { Need("n0", true) });
        var cand = SeedCandidate(tdb, camp.Id, "x@x.co",
            strengths: new List<NeedAssessment>(),                      // n0 must-have KHÔNG đạt
            gaps: new List<NeedAssessment> { Assess("n0", NeedLevels.Weak) });

        var res = await NewCampaignService(tdb.NewContext())
            .InviteShortlistedCandidatesAsync(org, org, camp.Id, new() { cand.Id }, includeIneligible: false, default);

        Assert.Empty(res.Invited);
        Assert.Contains("Không đủ điều kiện loại", Assert.Single(res.Failed).Reason);
        Assert.Empty(tdb.NewContext().CampaignInvitations.Where(i => i.CampaignId == camp.Id));
    }

    // ── 7. includeIneligible = true ⇒ mời được cả nhóm không đủ điều kiện ─────────────────────────
    [Fact]
    public async Task InviteShortlisted_IncludeIneligible_MoiDuoc()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org, needs: new List<JobNeed> { Need("n0", true) });
        var cand = SeedCandidate(tdb, camp.Id, "x@x.co",
            strengths: new List<NeedAssessment>(),
            gaps: new List<NeedAssessment> { Assess("n0", NeedLevels.Weak) });

        var res = await NewCampaignService(tdb.NewContext())
            .InviteShortlistedCandidatesAsync(org, org, camp.Id, new() { cand.Id }, includeIneligible: true, default);

        Assert.Single(res.Invited);
        Assert.Empty(res.Failed);
    }

    // ── 8. MỘT nguồn tính must_have: đường chấm LIVE == đường xem trước (byte-equal) ──────────────
    [Fact]
    public async Task MustHave_MotNguonTinh_LivePath_KhopPreviewPath()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org, needs: new List<JobNeed> { Need("n0", true), Need("n1", false) });

        // Mã hoá CẢ HAI biến trong dải [0,100] (Validate chạy trên ScoringContext.Sample: total 4,
        // met 3 ⇒ 43): must_have_total*10 + must_have_met ⇒ /10 = total, %10 = met.
        const string expr = "must_have_total * 10 + must_have_met";
        tdb.Db.ScoringPolicies.Add(new ScoringPolicy
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, Kind = ScoringExpressionKind.CvScreening,
            Version = 2, EngineVersion = "1", Name = "policy v2", Expression = expr,
            PassScorePct = 50, CreatedAt = DateTime.UtcNow, CreatedBy = Guid.NewGuid(),
        });
        var cand = new CvSubmission
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, Email = "c@x.co", CvParsedText = "CV",
            CvFileUrl = $"campaigns/{camp.Id}/candidates/x.pdf", ParseStatus = CvParseStatus.Done,
            Status = CvSubmissionStatus.Analyzing, ScoringPolicyVersion = 2,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        tdb.Db.CvSubmissions.Add(cand);
        tdb.Db.SaveChanges();

        // LIVE — CvScreeningService.ResolvePolicyScoreAsync
        await NewScreeningService(tdb.NewContext()).SaveCvResultAsync(cand.Id, new CvResultCallbackRequest
        {
            Assessments = new()
            {
                new NeedAssessmentItem { NeedId = "n0", Area = "a", Level = NeedLevels.Strong, Evidence = "x" },
                new NeedAssessmentItem { NeedId = "n1", Area = "b", Level = NeedLevels.Weak, Evidence = "x" },
            },
        }, default);
        var live = (await tdb.NewContext().CvSubmissions.SingleAsync(c => c.Id == cand.Id)).OverallMatchScore;

        // PREVIEW — ScoringPolicyService.ScoreCv
        var preview = await new ScoringPolicyService(tdb.NewContext()).PreviewPolicyAsync(
            org, camp.Id, new ScoringPolicyPreviewRequest { Kind = "CvScreening", Expression = expr },
            null, null, default);
        var previewScore = preview.Rows.Single().NewScore;

        // n0 là must-have DUY NHẤT, Strong ⇒ total 1, met 1 ⇒ 1*10 + 1 = 11.
        // Nếu còn "mọi nhu cầu coi là bắt buộc" (trước B5) thì total = 2 ⇒ 21.
        Assert.Equal(11m, previewScore);
        Assert.Equal(11, live);
        Assert.Equal((decimal?)live, previewScore);
    }

    // ── 9. Đổi isMustHave sau khi có người sàng ⇒ 409 (job_needs khoá — ReplaceJobNeedsAsync) ─────
    [Fact]
    public async Task ReplaceJobNeeds_DoiIsMustHaveSauKhiSang_409()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org, needs: new List<JobNeed> { Need("n0", mustHave: false) });
        SeedCandidate(tdb, camp.Id, "x@x.co", overallMatchScore: 70);   // đã sàng

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewCampaignService(tdb.NewContext()).ReplaceJobNeedsAsync(org, org, camp.Id, new List<JobNeedInput>
            {
                new() { NeedId = "n0", Category = JobNeedCategories.Technical, Text = "Nhu cầu n0", IsMustHave = true },
            }, default));

        Assert.False(tdb.NewContext().Campaigns.Single(c => c.Id == camp.Id).JobNeeds!.Single().IsMustHave);
    }

    // ── 10. GetCandidatesAsync mặc định: eligible=true đứng trước TRONG TRANG; cursor KHÔNG đổi ────
    [Fact]
    public async Task GetCandidates_MacDinh_EligibleTruocTrongTrang_CursorGiuHanhViCu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org, needs: new List<JobNeed> { Need("n0", true) });
        // Điểm cao NHƯNG rớt must-have.
        var hi = SeedCandidate(tdb, camp.Id, "hi@x.co", overallMatchScore: 95,
            strengths: new List<NeedAssessment>(),
            gaps: new List<NeedAssessment> { Assess("n0", NeedLevels.Weak) });
        // Điểm thấp NHƯNG đủ điều kiện.
        var lo = SeedCandidate(tdb, camp.Id, "lo@x.co", overallMatchScore: 40,
            strengths: new List<NeedAssessment> { Assess("n0", NeedLevels.Strong) },
            gaps: new List<NeedAssessment>());

        var page = await NewScreeningService(tdb.NewContext())
            .GetCandidatesAsync(org, camp.Id, null, null, null, null, null, null, null, default);

        // eligible=true (lo) đứng TRƯỚC eligible=false (hi) dù điểm thấp hơn.
        Assert.Equal(new[] { lo.Id, hi.Id }, page.Items.Select(i => i.Id));
        // 2 dòng < limit mặc định ⇒ hết trang (khoá keyset không đổi so với trước B5).
        Assert.Null(page.NextCursor);
    }

    // ── 11. CandidateListItem mang Eligible/MustHaveMet/MustHaveTotal ─────────────────────────────
    [Fact]
    public async Task GetCandidates_ItemMangEligibleVaMustHaveDeem()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org, needs: new List<JobNeed> { Need("n0", true), Need("n1", true) });
        var cand = SeedCandidate(tdb, camp.Id, "x@x.co", overallMatchScore: 60,
            strengths: new List<NeedAssessment> { Assess("n0", NeedLevels.Strong) },
            gaps: new List<NeedAssessment> { Assess("n1", NeedLevels.Weak) });

        var item = Assert.Single((await NewScreeningService(tdb.NewContext())
            .GetCandidatesAsync(org, camp.Id, null, null, null, null, null, null, null, default)).Items);

        Assert.False(item.Eligible);
        Assert.Equal(1, item.MustHaveMet);
        Assert.Equal(2, item.MustHaveTotal);
    }
}
