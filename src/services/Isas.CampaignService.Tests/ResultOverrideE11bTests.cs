using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// E11b — HR chốt/sửa điểm-kết-quả cuối (điểm AI = gợi ý). Override ghi cột trên campaign_rankings;
/// results đọc effective (override ?? AI) → điểm/rank/pass-fail đổi theo; clear = về AI; audit ghi;
/// ngoài org → 404; session không có ranking → 404; Note bắt buộc; Result chỉ Pass/Fail.
/// </summary>
public class ResultOverrideE11bTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static Campaign SeedCampaign(CampaignDbContext db, Guid orgId, int? pass = null)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        c.PassScorePct = pass;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static CampaignRanking SeedRanking(CampaignDbContext db, Guid campaignId, decimal score, Guid? sessionId = null)
    {
        var r = new CampaignRanking
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = Guid.NewGuid(),
            SessionId = sessionId ?? Guid.NewGuid(),
            TotalScore = score,
            UpdatedAt = DateTime.UtcNow
        };
        db.CampaignRankings.Add(r);
        db.SaveChanges();
        return r;
    }

    [Fact]
    public async Task Override_score_changes_effective_score_and_rank_and_records_audit()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var low = SeedRanking(tdb.Db, campaign.Id, 40.00m);   // AI Fail, rank 2
        var high = SeedRanking(tdb.Db, campaign.Id, 80.00m);  // AI Pass, rank 1

        // HR đẩy 'low' lên 95 + Pass → phải vượt 'high'.
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, actor, campaign.Id, low.SessionId,
            new OverrideResultRequest { Score = 95.00m, Result = "Pass", Note = "Phỏng vấn trực tiếp rất tốt" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        var top = res.Results[0];
        Assert.Equal(low.SessionId, top.SessionId);       // low lên #1
        Assert.Equal(1, top.Rank);
        Assert.Equal(95.00m, top.TotalScore);             // effective
        Assert.Equal(40.00m, top.AiScore);                // AI gốc giữ nguyên
        Assert.Equal("Pass", top.Result);
        Assert.Equal(95.00m, top.OverrideScore);
        Assert.NotNull(top.OverriddenAt);

        // audit ghi 1 dòng OverrideResult
        using var verify = tdb.NewContext();
        Assert.Contains(verify.AuditLogs, a => a.Action == AuditAction.OverrideResult && a.EntityId == campaign.Id && a.ActorUserId == actor);
    }

    [Fact]
    public async Task Override_result_only_forces_passfail_over_threshold()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var r = SeedRanking(tdb.Db, campaign.Id, 80.00m);   // AI Pass theo ngưỡng

        // HR ép Fail dù điểm 80 ≥ 50.
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Result = "Fail", Note = "Phát hiện gian lận" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        Assert.Equal("Fail", res.Results[0].Result);
        Assert.Equal(80.00m, res.Results[0].TotalScore);   // điểm không đổi (chỉ ép result)
    }

    [Fact]
    public async Task Clear_override_reverts_to_ai()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId, pass: 50);
        var r = SeedRanking(tdb.Db, campaign.Id, 40.00m);

        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Score = 90m, Result = "Pass", Note = "tốt" }, default);
        // Clear
        await NewService(tdb.NewContext()).OverrideResultAsync(
            orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
            new OverrideResultRequest { Score = null, Result = null, Note = "huỷ điều chỉnh" }, default);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, campaign.Id, default);
        Assert.Equal(40.00m, res.Results[0].TotalScore);   // về AI
        Assert.Null(res.Results[0].OverrideScore);
        Assert.Equal("Fail", res.Results[0].Result);        // theo ngưỡng lại
    }

    [Fact]
    public async Task Override_outside_org_throws_404()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaign(tdb.Db, Guid.NewGuid());
        var r = SeedRanking(tdb.Db, campaign.Id, 50m);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext()).OverrideResultAsync(
                Guid.NewGuid() /* org khác */, Guid.NewGuid(), campaign.Id, r.SessionId,
                new OverrideResultRequest { Score = 90m, Note = "x" }, default));
    }

    [Fact]
    public async Task Override_missing_ranking_throws_404()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext()).OverrideResultAsync(
                orgId, Guid.NewGuid(), campaign.Id, Guid.NewGuid() /* session không có ranking */,
                new OverrideResultRequest { Score = 90m, Note = "x" }, default));
    }

    [Theory]
    [InlineData("", "Pass")]        // Note rỗng
    [InlineData("lý do", "Maybe")]  // Result sai
    public async Task Override_invalid_input_throws_argument(string note, string result)
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var r = SeedRanking(tdb.Db, campaign.Id, 50m);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).OverrideResultAsync(
                orgId, Guid.NewGuid(), campaign.Id, r.SessionId,
                new OverrideResultRequest { Score = 90m, Result = result, Note = note }, default));
    }
}
