using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// RNK1 · HĐ-5 — điểm sàn theo tiêu chí (<c>campaign_criteria.min_pct</c>), id tiêu chí ỔN ĐỊNH qua
/// PUT, và <c>belowCutoff</c> áp READ-TIME.
///
/// <para>Sàn là luật KẾT LUẬN (như <c>pass_score_pct</c>): đọc từ campaign_criteria HIỆN TẠI mỗi lần
/// dựng bảng kết quả ⇒ HR sửa <c>min_pct</c> là áp NGAY cho cả ứng viên đã thi, KHÔNG bump
/// <c>rubric_version</c>, KHÔNG hạ rank. Khớp snapshot ↔ campaign_criteria theo <c>criterionId</c>
/// (ổn định qua PUT); snapshot ghi TRƯỚC RNK1 không có id ⇒ lùi về khớp theo TÊN.</para>
/// </summary>
public class CriterionMinPctRnk1B4Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static CampaignCriterion SeedCriterion(
        Guid campaignId, string name, decimal weight = 1.0m, int maxScore = 5,
        int order = 0, int? minPct = null)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = order, Name = name,
            Weight = weight, MaxScore = maxScore, MinPct = minPct, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };

    private static Campaign SeedCampaign(
        CampaignDbContext db, Guid orgId, CampaignStatus status,
        int? passScorePct = null, params CampaignCriterion[] criteria)
    {
        var c = CampaignTestDb.NewCampaign(orgId, status);
        c.PassScorePct = passScorePct;
        foreach (var cr in criteria) { cr.CampaignId = c.Id; c.Criteria.Add(cr); }
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static CriterionItem Item(string name, Guid? id = null, int? minPct = null,
        decimal weight = 1.0m, int maxScore = 5)
        => new() { Id = id, Name = name, Weight = weight, MaxScore = maxScore, MinPct = minPct };

    private static void SeedRanking(
        CampaignDbContext db, Guid campaignId, Guid candidateId, decimal totalScore,
        ScoringInputsSnapshot? snapshot, string? overrideResult = null)
    {
        db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = candidateId,
            SessionId = Guid.NewGuid(), TotalScore = totalScore, ScoringInputs = snapshot,
            OverrideResult = overrideResult, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static ScoringInputsSnapshot Snap(string critName, decimal pct, Guid? critId)
        => new(
            new[] { new CriterionInputSnapshot(critName, pct, 1.0m, 5, CriterionId: critId) },
            Answered: 5, TotalQuestions: 5);

    // ── id ỔN ĐỊNH qua PUT ───────────────────────────────────────────────────────────────────────

    // Echo lại id tiêu chí đang có ⇒ GIỮ id (update tại chỗ) — khoá ổn định để snapshot chấm khớp về.
    [Fact]
    public async Task PUT_EchoId_GiuNguyenId()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Draft, criteria: SeedCriterion(Guid.Empty, "A"));
        var oldId = tdb.Db.CampaignCriteria.Single(c => c.CampaignId == camp.Id).Id;

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Criteria = new List<CriterionItem> { Item("A", id: oldId) } }, default);

        var after = tdb.NewContext().CampaignCriteria.Single(c => c.CampaignId == camp.Id);
        Assert.Equal(oldId, after.Id);
    }

    // Không echo id ⇒ replace-all: tiêu chí cũ bị xoá, tiêu chí mới nhận id mới.
    [Fact]
    public async Task PUT_KhongEchoId_IdMoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Draft, criteria: SeedCriterion(Guid.Empty, "A"));
        var oldId = tdb.Db.CampaignCriteria.Single(c => c.CampaignId == camp.Id).Id;

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Criteria = new List<CriterionItem> { Item("A") } }, default);

        var after = tdb.NewContext().CampaignCriteria.Single(c => c.CampaignId == camp.Id);
        Assert.NotEqual(oldId, after.Id);
    }

    // id lạ (không thuộc campaign) ⇒ KHÔNG tái dùng — tiêu chí nhận id mới sinh, không phải id lạ.
    [Fact]
    public async Task PUT_IdLa_IdMoiKhongPhaiIdLa()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Draft, criteria: SeedCriterion(Guid.Empty, "A"));
        var laId = Guid.NewGuid();

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Criteria = new List<CriterionItem> { Item("A", id: laId) } }, default);

        var after = tdb.NewContext().CampaignCriteria.Single(c => c.CampaignId == camp.Id);
        Assert.NotEqual(laId, after.Id);
    }

    // ── validate minPct 0..100 ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(150)]
    public async Task PUT_MinPctNgoaiKhoang_400_KemTenTieuChi(int badMinPct)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Draft, criteria: SeedCriterion(Guid.Empty, "Giao tiếp"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
                new UpdateCampaignRequest { Criteria = new List<CriterionItem> { Item("Giao tiếp", minPct: badMinPct) } },
                default));

        Assert.Contains("Giao tiếp", ex.Message);
        Assert.Contains("[0, 100]", ex.Message);
    }

    [Fact]
    public async Task PUT_MinPctHopLe_GhiXuongDb()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Draft, criteria: SeedCriterion(Guid.Empty, "A"));

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Criteria = new List<CriterionItem> { Item("A", minPct: 50) } }, default);

        Assert.Equal(50, tdb.NewContext().CampaignCriteria.Single(c => c.CampaignId == camp.Id).MinPct);
    }

    // ── vân tay KHÔNG gồm minPct ────────────────────────────────────────────────────────────────

    // Vân tay được tính từ OrderNo/Name/Description/Weight/MaxScore/Levels — KHÔNG có min_pct.
    [Fact]
    public void Vantay_KHONG_doi_khi_chi_minPct_doi()
    {
        var cid = Guid.NewGuid();
        var khongSan = SeedCriterion(cid, "A", minPct: null);
        var coSan = SeedCriterion(cid, "A", minPct: 60);
        coSan.Id = khongSan.Id;

        Assert.Equal(
            RubricFingerprint.Compute(new[] { khongSan }),
            RubricFingerprint.Compute(new[] { coSan }));
    }

    // Campaign Active: sửa CHỈ min_pct ⇒ KHÔNG bump rubric_version (sàn là luật kết luận, không thước đo).
    [Fact]
    public async Task Active_SuaChiMinPct_KHONG_BumpRubricVersion()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var crit = SeedCriterion(Guid.Empty, "A");
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Active, criteria: crit);
        var critId = tdb.Db.CampaignCriteria.Single(c => c.CampaignId == camp.Id).Id;

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Criteria = new List<CriterionItem> { Item("A", id: critId, minPct: 50) } },
            default);

        using var check = tdb.NewContext();
        Assert.Equal(1, check.Campaigns.Single(c => c.Id == camp.Id).RubricVersion);
        Assert.Equal(50, check.CampaignCriteria.Single(c => c.CampaignId == camp.Id).MinPct);
    }

    // ── belowCutoff READ-TIME trong bảng kết quả ────────────────────────────────────────────────

    // pct 40 của tiêu chí có min_pct 50 ⇒ rớt SÀN ⇒ Result = "Fail" DÙ điểm tổng 80 ≥ ngưỡng 60.
    [Fact]
    public async Task Results_Pct40_MinPct50_Fail_BelowCutoff_DuTotalVuotNguong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var crit = SeedCriterion(Guid.Empty, "Giao tiếp", minPct: 50);
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Active, passScorePct: 60, criteria: crit);
        var critId = tdb.Db.CampaignCriteria.Single(c => c.CampaignId == camp.Id).Id;
        var cand = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, cand, totalScore: 80m, Snap("Giao tiếp", 40m, critId));

        var row = Assert.Single((await NewService(tdb.NewContext())
            .GetCampaignResultsAsync(org, camp.Id, default)).Results);

        Assert.Equal("Fail", row.Result);
        var b = Assert.Single(row.BelowCutoff);
        Assert.Equal(critId, b.CriterionId);
        Assert.Equal("Giao tiếp", b.Name);
        Assert.Equal(40m, b.Pct);
        Assert.Equal(50, b.MinPct);
        Assert.Equal("id", b.MatchedBy);
    }

    // HR override "Pass" thắng luật sàn (belowCutoff vẫn hiển thị để HR thấy vì sao mình override).
    [Fact]
    public async Task Results_OverridePass_ThangLuatSan()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var crit = SeedCriterion(Guid.Empty, "Giao tiếp", minPct: 50);
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Active, passScorePct: 60, criteria: crit);
        var critId = tdb.Db.CampaignCriteria.Single(c => c.CampaignId == camp.Id).Id;
        SeedRanking(tdb.Db, camp.Id, Guid.NewGuid(), 80m, Snap("Giao tiếp", 40m, critId), overrideResult: "Pass");

        var row = Assert.Single((await NewService(tdb.NewContext())
            .GetCampaignResultsAsync(org, camp.Id, default)).Results);

        Assert.Equal("Pass", row.Result);
        Assert.Single(row.BelowCutoff);
    }

    // Rớt sàn ĐỔI KẾT LUẬN chứ KHÔNG đổi thứ hạng: ứng viên điểm cao nhất mà rớt sàn vẫn rank 1.
    [Fact]
    public async Task Results_RotSan_KHONG_HaRank()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var crit = SeedCriterion(Guid.Empty, "Giao tiếp", minPct: 50);
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Active, passScorePct: 60, criteria: crit);
        var critId = tdb.Db.CampaignCriteria.Single(c => c.CampaignId == camp.Id).Id;
        var top = Guid.NewGuid();
        var second = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, top, 80m, Snap("Giao tiếp", 40m, critId));      // rớt sàn
        SeedRanking(tdb.Db, camp.Id, second, 70m, Snap("Giao tiếp", 90m, critId));   // qua sàn

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(org, camp.Id, default);
        var topRow = res.Results.Single(r => r.CandidateId == top);
        var secondRow = res.Results.Single(r => r.CandidateId == second);

        Assert.Equal(1, topRow.Rank);
        Assert.Equal("Fail", topRow.Result);
        Assert.Equal(2, secondRow.Rank);
    }

    // Snapshot GHI TRƯỚC RNK1 (CriterionId = null) ⇒ khớp campaign_criteria theo TÊN (Trim/IgnoreCase).
    [Fact]
    public async Task Results_SnapshotCu_KhongCriterionId_KhopTheoTen()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var crit = SeedCriterion(Guid.Empty, "Giao tiếp", minPct: 50);
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Active, passScorePct: 60, criteria: crit);
        SeedRanking(tdb.Db, camp.Id, Guid.NewGuid(), 80m, Snap("  giao tiếp  ", 40m, critId: null));

        var row = Assert.Single((await NewService(tdb.NewContext())
            .GetCampaignResultsAsync(org, camp.Id, default)).Results);

        var b = Assert.Single(row.BelowCutoff);
        Assert.Equal("name", b.MatchedBy);
        Assert.Equal("Giao tiếp", b.Name);   // tên chuẩn của campaign_criteria, không phải chuỗi trong snapshot
    }

    // HR đổi min_pct khi Active ⇒ ứng viên ĐÃ THI cũng đổi kết luận (read-time, ranking row không đụng).
    [Fact]
    public async Task Results_DoiMinPctKhiActive_NguoiDaThi_DoiKetLuan()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var crit = SeedCriterion(Guid.Empty, "Giao tiếp", minPct: 30);
        var camp = SeedCampaign(tdb.Db, org, CampaignStatus.Active, passScorePct: 60, criteria: crit);
        var critId = tdb.Db.CampaignCriteria.Single(c => c.CampaignId == camp.Id).Id;
        SeedRanking(tdb.Db, camp.Id, Guid.NewGuid(), 80m, Snap("Giao tiếp", 40m, critId));   // pct 40 ≥ 30 ⇒ qua

        var before = Assert.Single((await NewService(tdb.NewContext())
            .GetCampaignResultsAsync(org, camp.Id, default)).Results);
        Assert.Empty(before.BelowCutoff);
        Assert.Equal("Pass", before.Result);   // 80 ≥ 60

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id,
            new UpdateCampaignRequest { Criteria = new List<CriterionItem> { Item("Giao tiếp", id: critId, minPct: 50) } },
            default);

        var after = Assert.Single((await NewService(tdb.NewContext())
            .GetCampaignResultsAsync(org, camp.Id, default)).Results);
        Assert.Single(after.BelowCutoff);   // pct 40 < 50 ⇒ nay rớt sàn
        Assert.Equal("Fail", after.Result);
    }
}
