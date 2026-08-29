using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCP1-B7 — CvScreeningService.SaveCvResultAsync dùng BIỂU THỨC ĐÃ GHIM (B5) để tính điểm sàng CV.
/// Điểm TÍNH từ mức bằng chứng (CAMP-14, KHÔNG nhận số của AI). Lùi an toàn + cờ như B6.
/// need_count = 0 ⇒ BÁO LỖI. Đổi policy KHÔNG hồi tố ứng viên đã ghim.
/// </summary>
public class ScoringPolicyCvB7Tests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:CallbackBase"] = "http://campaign:8080" })
            .Build();

    private static CvScreeningService NewService(CampaignDbContext db) =>
        new(db, Mock.Of<ICvScreeningPublisher>(), Config(), Mock.Of<ILogger<CvScreeningService>>());

    private static NeedAssessmentItem Assess(string needId, string level)
        => new() { NeedId = needId, Area = $"vùng {needId}", Level = level, Evidence = "trích từ CV" };

    private static Campaign SeedCampaign(CampaignTestDb tdb, Guid owner, int needCount, int? cvPolicyVersion = null)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.Domain = "BE";
        camp.JDText = "JD";
        camp.CvPolicyVersion = cvPolicyVersion;
        camp.JobNeeds = Enumerable.Range(0, needCount).Select(i => new JobNeed
        {
            NeedId = $"need-{i}", Category = JobNeedCategories.Technical, Text = $"Nhu cầu {i}",
            Source = JobNeedSources.HrEdited,
        }).ToList();
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    private static void SeedPolicy(CampaignTestDb tdb, Guid campaignId, int version, string expr)
    {
        tdb.Db.ScoringPolicies.Add(new ScoringPolicy
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Kind = ScoringExpressionKind.CvScreening,
            Version = version, EngineVersion = "1", Name = $"policy v{version}",
            Expression = expr, PassScorePct = 50, CreatedAt = DateTime.UtcNow, CreatedBy = Guid.NewGuid(),
        });
        tdb.Db.SaveChanges();
    }

    private static CvSubmission SeedCandidate(CampaignTestDb tdb, Guid campaignId, int? pinnedPolicyVersion)
    {
        var now = DateTime.UtcNow;
        var cand = new CvSubmission
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Email = $"c{Guid.NewGuid():N}@x.com",
            CvParsedText = "CV text", CvFileUrl = $"campaigns/{campaignId}/candidates/x.pdf",
            ParseStatus = CvParseStatus.Done, Status = CvSubmissionStatus.Analyzing,
            ScoringPolicyVersion = pinnedPolicyVersion, CreatedAt = now, UpdatedAt = now,
        };
        tdb.Db.CvSubmissions.Add(cand);
        tdb.Db.SaveChanges();
        return cand;
    }

    private static async Task<CvSubmission> Save(CampaignTestDb tdb, Guid candidateId, params NeedAssessmentItem[] a)
    {
        await NewService(tdb.NewContext()).SaveCvResultAsync(candidateId,
            new CvResultCallbackRequest { Assessments = a.ToList() }, default);
        return await tdb.NewContext().CvSubmissions.SingleAsync(c => c.Id == candidateId);
    }

    private const string MustHaveGate =
        "if(must_have_met < must_have_total, 0, 100 * (strong_count + 0.5 * partial_count) / need_count)";

    // ── (test brief) must-have gate — thiếu must-have được 0 ─────────────────────────────────
    [Fact]
    public async Task MustHaveGate_ThieuBangChung_Duoc0()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, needCount: 2);
        SeedPolicy(tdb, camp.Id, 2, MustHaveGate);
        var cand = SeedCandidate(tdb, camp.Id, pinnedPolicyVersion: 2);

        // need-0 Strong, need-1 Weak ⇒ must_have_met (1) < must_have_total (2) ⇒ GATE ⇒ 0.
        var after = await Save(tdb, cand.Id, Assess("need-0", NeedLevels.Strong), Assess("need-1", NeedLevels.Weak));

        Assert.Equal(0, after.OverallMatchScore);
        Assert.False(after.ScoreFallback);
    }

    [Fact]
    public async Task MustHaveGate_DuBangChung_QuaGate()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, needCount: 2);
        SeedPolicy(tdb, camp.Id, 2, MustHaveGate);
        var cand = SeedCandidate(tdb, camp.Id, pinnedPolicyVersion: 2);

        // cả 2 Strong ⇒ must_have_met (2) = must_have_total (2) ⇒ 100 * (2 + 0) / 2 = 100.
        var after = await Save(tdb, cand.Id, Assess("need-0", NeedLevels.Strong), Assess("need-1", NeedLevels.Strong));

        Assert.Equal(100, after.OverallMatchScore);
        Assert.False(after.ScoreFallback);
    }

    // ── (test brief) đổi policy campaign → ứng viên ĐÃ GHIM giữ nguyên điểm ──────────────────
    [Fact]
    public async Task DoiPolicyCampaign_UngVienDaGhim_GiuNguyenDiem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        // con trỏ campaign = v5 (biểu thức KHÁC HẲN), nhưng ứng viên ghim v2.
        var camp = SeedCampaign(tdb, owner, needCount: 2, cvPolicyVersion: 5);
        SeedPolicy(tdb, camp.Id, 2, "strong_count * 10");    // v2 — bản ĐÃ GHIM
        SeedPolicy(tdb, camp.Id, 5, "strong_count * 100");   // v5 — con trỏ hiện hành
        var cand = SeedCandidate(tdb, camp.Id, pinnedPolicyVersion: 2);

        var after = await Save(tdb, cand.Id, Assess("need-0", NeedLevels.Strong));   // strong_count = 1

        Assert.Equal(10, after.OverallMatchScore);   // 1 * 10 (v2 ĐÃ GHIM) — KHÔNG phải 100 (v5 con trỏ)

        // callback tới lần nữa (redelivery) SAU khi con trỏ đã đổi → vẫn v2 → điểm KHÔNG đổi.
        var again = await Save(tdb, cand.Id, Assess("need-0", NeedLevels.Strong));
        Assert.Equal(10, again.OverallMatchScore);
    }

    // ── (test brief) need_count = 0 → BÁO LỖI ĐÁNH GIÁ, KHÔNG bịa điểm ──────────────────────
    [Fact]
    public async Task NeedCount0_BaoLoi_KhongLuiKhongBiaDiem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, needCount: 0);   // bất biến bị vi phạm (EVA1-B6)
        SeedPolicy(tdb, camp.Id, 2, "100 * strong_count / need_count");
        var cand = SeedCandidate(tdb, camp.Id, pinnedPolicyVersion: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext()).SaveCvResultAsync(cand.Id,
                new CvResultCallbackRequest { Assessments = new() }, default));

        var after = await tdb.NewContext().CvSubmissions.SingleAsync(c => c.Id == cand.Id);
        Assert.Null(after.OverallMatchScore);                       // KHÔNG bịa
        Assert.Equal(CvSubmissionStatus.Analyzing, after.Status);   // chưa lật sang Analyzed
    }

    // ── lùi an toàn: chia 0 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Chia0_LuiVeCAMP14_CoBat_KhongNem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, needCount: 1);
        SeedPolicy(tdb, camp.Id, 2, "100 / 0");
        var cand = SeedCandidate(tdb, camp.Id, pinnedPolicyVersion: 2);

        // 1 Strong ⇒ CAMP-14 mặc định = 100 * 1 / 1 = 100.
        var after = await Save(tdb, cand.Id, Assess("need-0", NeedLevels.Strong));

        Assert.Equal(100, after.OverallMatchScore);   // = CAMP-14 mặc định
        Assert.True(after.ScoreFallback);
    }

    // ── lùi an toàn: kết quả ngoài [0,100] — KHÔNG clamp ───────────────────────────────────
    [Fact]
    public async Task KetQuaNgoaiDai_LuiVeCAMP14_KhongClamp()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, needCount: 2);
        SeedPolicy(tdb, camp.Id, 2, "strong_count * 1000");   // 1 * 1000 = 1000 → RESULT_OUT_OF_RANGE
        var cand = SeedCandidate(tdb, camp.Id, pinnedPolicyVersion: 2);

        // 1 Strong + 1 Weak assessed ⇒ CAMP-14 mặc định = 100 * 1 / 2 = 50.
        var after = await Save(tdb, cand.Id, Assess("need-0", NeedLevels.Strong), Assess("need-1", NeedLevels.Weak));

        Assert.Equal(50, after.OverallMatchScore);   // lùi về mặc định — KHÔNG phải 100 (clamp che lỗi)
        Assert.True(after.ScoreFallback);
    }

    // ── (5) chưa ghim policy → CAMP-14 mặc định, cờ tắt (tương thích ngược) ─────────────────
    [Fact]
    public async Task ChuaGhimPolicy_DungCAMP14_KhongBatCo()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedCampaign(tdb, owner, needCount: 2);
        var cand = SeedCandidate(tdb, camp.Id, pinnedPolicyVersion: null);

        var after = await Save(tdb, cand.Id, Assess("need-0", NeedLevels.Strong), Assess("need-1", NeedLevels.Partial));

        // CAMP-14: 100 * (1 + 0.5) / 2 = 75.
        Assert.Equal(75, after.OverallMatchScore);
        Assert.False(after.ScoreFallback);
    }
}
