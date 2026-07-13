using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// C15 (c) — Filter shortlist GET /campaign/{id}/candidates: ?status=&amp;minScore=&amp;skill=
/// (mở rộng endpoint C14 sẵn có). minScore lọc overall_match_score; status lọc CandidateStatus;
/// skill = Skills chứa (client-eval, portable SQLite/Npgsql). Mặc định sort=score DESC.
/// </summary>
public class CampaignCandidateFilterTests
{
    private static CvScreeningService NewService(CampaignDbContext db) =>
        new(db, Mock.Of<ICvScreeningPublisher>(),
            new ConfigurationBuilder().Build(), Mock.Of<ILogger<CvScreeningService>>());

    private static Campaign SeedActiveCampaign(CampaignTestDb tdb, Guid owner)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    private static void SeedCandidate(
        CampaignTestDb tdb, Guid campaignId, CandidateStatus status,
        string email, int? overall, List<string>? skills)
    {
        var now = DateTime.UtcNow;
        tdb.Db.CampaignCandidates.Add(new CampaignCandidate
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Email = email,
            CvParsedText = "CV",
            ParseStatus = CvParseStatus.Done,
            Status = status,
            OverallMatchScore = overall,
            Skills = skills,
            CreatedAt = now,
            UpdatedAt = now
        });
        tdb.Db.SaveChanges();
    }

    // skill: chỉ ứng viên có Skills chứa "SQL" (case-insensitive) — client-eval portable.
    [Fact]
    public async Task Filter_skill_loc_dung_tap()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "a@x.com", 80, new() { "C#", "SQL" });
        SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "b@x.com", 70, new() { "Java", "React" });
        SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "c@x.com", 60, null);

        var svc = NewService(tdb.NewContext());
        var list = await svc.GetCandidatesAsync(owner, camp.Id, null, null, "sql", "score", default);

        Assert.Single(list);
        Assert.Equal("a@x.com", list[0].Email);
    }

    // status: chỉ ứng viên đúng CandidateStatus (Analyzed) — bỏ Filtered/Rejected.
    [Fact]
    public async Task Filter_status_loc_dung_tap()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "a@x.com", 80, null);
        SeedCandidate(tdb, camp.Id, CandidateStatus.Filtered, "b@x.com", null, null);
        SeedCandidate(tdb, camp.Id, CandidateStatus.Rejected, "c@x.com", null, null);

        var svc = NewService(tdb.NewContext());
        var list = await svc.GetCandidatesAsync(owner, camp.Id, "Analyzed", null, null, "score", default);

        Assert.Single(list);
        Assert.Equal("a@x.com", list[0].Email);
        Assert.Equal("Analyzed", list[0].Status);
    }

    // combo: status + minScore + skill giao nhau → đúng 1 ứng viên; sort=score DESC giữ nguyên.
    [Fact]
    public async Task Filter_combo_status_minScore_skill()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "hit@x.com", 90, new() { "SQL", "Azure" });
        SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "lowscore@x.com", 50, new() { "SQL" });        // rớt minScore
        SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzed, "noskill@x.com", 95, new() { "Java" });        // rớt skill
        SeedCandidate(tdb, camp.Id, CandidateStatus.Analyzing, "wrongstatus@x.com", 99, new() { "SQL" });    // rớt status

        var svc = NewService(tdb.NewContext());
        var list = await svc.GetCandidatesAsync(owner, camp.Id, "Analyzed", 70, "sql", "score", default);

        Assert.Single(list);
        Assert.Equal("hit@x.com", list[0].Email);
    }
}
