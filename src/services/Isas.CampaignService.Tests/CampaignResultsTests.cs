using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// E5 — GET /campaign/{id}/results: xếp hạng + pass/fail, đọc read-model campaign_rankings (E4 upsert).
/// (a) sắp giảm theo total_score + gán rank đúng (đồng điểm → cùng rank: 1,1,3);
/// (b) pass/fail đúng theo ngưỡng Employer (pass_score_pct); ngưỡng null → Result null (HR quyết tay);
/// (c) ứng viên chưa Scored (không có row ranking) KHÔNG xuất hiện + không lẫn ranking campaign khác;
/// (d) người ngoài org (employer_id khác) → không xem được (KeyNotFoundException → 404).
/// </summary>
public class CampaignResultsTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static Campaign SeedCampaign(CampaignDbContext db, Guid employerId, int? passScorePct = null)
    {
        var c = CampaignTestDb.NewCampaign(employerId, CampaignStatus.Active);
        c.PassScorePct = passScorePct;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static CampaignRanking SeedRanking(
        CampaignDbContext db, Guid campaignId, decimal score,
        DateTime? scoredAt = null, Guid? candidateId = null, Guid? sessionId = null)
    {
        var r = new CampaignRanking
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId ?? Guid.NewGuid(),
            SessionId = sessionId ?? Guid.NewGuid(),
            TotalScore = score,
            UpdatedAt = scoredAt ?? DateTime.UtcNow
        };
        db.CampaignRankings.Add(r);
        db.SaveChanges();
        return r;
    }

    // (a) Results sắp giảm theo total_score + rank đúng, đồng điểm → cùng rank (competition: 1,1,3).
    [Fact]
    public async Task Results_sap_giam_theo_total_score_va_rank_dong_hang_dung()
    {
        using var tdb = new CampaignTestDb();
        var employerId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, employerId);

        var t0 = DateTime.UtcNow;
        var cTop = SeedRanking(tdb.Db, campaign.Id, 90.00m, t0);
        // Hai ứng viên đồng điểm 82.50 — UpdatedAt khác nhau để thứ tự tie-break ổn định.
        var cTieEarly = SeedRanking(tdb.Db, campaign.Id, 82.50m, t0.AddMinutes(1));
        var cTieLate = SeedRanking(tdb.Db, campaign.Id, 82.50m, t0.AddMinutes(2));
        var cBottom = SeedRanking(tdb.Db, campaign.Id, 70.00m, t0.AddMinutes(3));

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(employerId, campaign.Id, default);

        Assert.Equal(4, res.TotalCandidates);
        Assert.Equal(4, res.Results.Count);

        // Sắp giảm dần theo total_score.
        var scores = res.Results.Select(r => r.TotalScore).ToList();
        Assert.Equal(new[] { 90.00m, 82.50m, 82.50m, 70.00m }, scores);

        // Rank: 1, 2, 2 (đồng hạng), 4 (nhảy theo vị trí).
        Assert.Equal(new[] { 1, 2, 2, 4 }, res.Results.Select(r => r.Rank).ToList());

        // Tie-break: ứng viên Scored sớm hơn (UpdatedAt nhỏ hơn) đứng trước.
        Assert.Equal(cTop.CandidateId, res.Results[0].CandidateId);
        Assert.Equal(cTieEarly.CandidateId, res.Results[1].CandidateId);
        Assert.Equal(cTieLate.CandidateId, res.Results[2].CandidateId);
        Assert.Equal(cBottom.CandidateId, res.Results[3].CandidateId);
    }

    // (b) Pass/fail theo ngưỡng: total_score >= pass_score_pct → Pass (boundary = Pass), < → Fail.
    [Fact]
    public async Task Results_pass_fail_dung_theo_nguong_employer()
    {
        using var tdb = new CampaignTestDb();
        var employerId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, employerId, passScorePct: 80);

        SeedRanking(tdb.Db, campaign.Id, 85.00m);   // > ngưỡng → Pass
        SeedRanking(tdb.Db, campaign.Id, 80.00m);   // = ngưỡng → Pass (boundary)
        SeedRanking(tdb.Db, campaign.Id, 79.99m);   // < ngưỡng → Fail

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(employerId, campaign.Id, default);

        Assert.Equal(80, res.PassScorePct);
        Assert.Equal("Pass", res.Results.Single(r => r.TotalScore == 85.00m).Result);
        Assert.Equal("Pass", res.Results.Single(r => r.TotalScore == 80.00m).Result);
        Assert.Equal("Fail", res.Results.Single(r => r.TotalScore == 79.99m).Result);
    }

    // (b') Ngưỡng chưa đặt (null) → mọi Result = null (HR quyết tay — doc §pass_score_pct).
    [Fact]
    public async Task Results_nguong_null_thi_result_null()
    {
        using var tdb = new CampaignTestDb();
        var employerId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, employerId, passScorePct: null);

        SeedRanking(tdb.Db, campaign.Id, 95.00m);
        SeedRanking(tdb.Db, campaign.Id, 10.00m);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(employerId, campaign.Id, default);

        Assert.Null(res.PassScorePct);
        Assert.All(res.Results, r => Assert.Null(r.Result));
    }

    // (c) Ứng viên chưa Scored (không có row ranking) KHÔNG xuất hiện; ranking campaign khác không lẫn.
    [Fact]
    public async Task Results_chi_gom_scored_cua_dung_campaign()
    {
        using var tdb = new CampaignTestDb();
        var employerId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, employerId);
        var other = SeedCampaign(tdb.Db, employerId);

        var scored1 = SeedRanking(tdb.Db, campaign.Id, 60.00m);
        var scored2 = SeedRanking(tdb.Db, campaign.Id, 88.00m);
        // Ứng viên "chưa Scored" = KHÔNG có row campaign_rankings (row chỉ tạo khi SessionScored).
        var notScoredCandidate = Guid.NewGuid();
        // Ranking thuộc campaign khác — không được lẫn vào kết quả campaign này.
        SeedRanking(tdb.Db, other.Id, 99.00m);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(employerId, campaign.Id, default);

        Assert.Equal(2, res.TotalCandidates);
        var candidateIds = res.Results.Select(r => r.CandidateId).ToHashSet();
        Assert.Contains(scored1.CandidateId, candidateIds);
        Assert.Contains(scored2.CandidateId, candidateIds);
        Assert.DoesNotContain(notScoredCandidate, candidateIds);
        // Không có điểm 99.00 (của campaign khác).
        Assert.DoesNotContain(res.Results, r => r.TotalScore == 99.00m);
    }

    // (d) Người ngoài org (employer_id khác) → không xem được kết quả (404 = KeyNotFoundException).
    [Fact]
    public async Task Results_nguoi_ngoai_org_khong_xem_duoc()
    {
        using var tdb = new CampaignTestDb();
        var ownerId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, ownerId);
        SeedRanking(tdb.Db, campaign.Id, 88.00m);

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => svc.GetCampaignResultsAsync(outsiderId, campaign.Id, default));
    }
}
