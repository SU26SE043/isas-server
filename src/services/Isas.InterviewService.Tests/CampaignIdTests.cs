using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Engine dùng chung B2B/B2C (decisions.md D1): phân biệt bằng campaign_id trên session.
/// Xác nhận cột nullable campaign_id round-trip 2 chiều, không phá luồng B2C (null).
/// </summary>
public class CampaignIdTests
{
    [Fact]
    public async Task B2C_Session_CampaignId_Null_RoundTrips()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready); // CampaignId mặc định null

        t.Db.PracticeSessions.Add(session);
        await t.Db.SaveChangesAsync();

        // Đọc bằng context mới -> tránh identity-map cache, ép load từ DB.
        await using var read = t.NewContext();
        var saved = await read.PracticeSessions.AsNoTracking()
            .SingleAsync(s => s.Id == session.Id);

        Assert.Null(saved.CampaignId);
    }

    [Fact]
    public async Task B2B_Session_CampaignId_Persists_And_Queryable()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready);
        session.CampaignId = campaignId;

        t.Db.PracticeSessions.Add(session);
        await t.Db.SaveChangesAsync();

        await using var read = t.NewContext();
        var byCampaign = await read.PracticeSessions.AsNoTracking()
            .SingleAsync(s => s.CampaignId == campaignId);

        Assert.Equal(session.Id, byCampaign.Id);
        Assert.Equal(campaignId, byCampaign.CampaignId);
    }

    [Fact]
    public async Task RubricCriterion_CampaignId_RoundTrips()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();

        var b2cCriterion = TestDb.Criterion(JobCategory.BE);           // CampaignId null = rubric B2C
        var b2bCriterion = TestDb.Criterion(JobCategory.BE);
        b2bCriterion.CampaignId = campaignId;

        t.Db.RubricCriteria.AddRange(b2cCriterion, b2bCriterion);
        await t.Db.SaveChangesAsync();

        await using var read = t.NewContext();
        Assert.Null((await read.RubricCriteria.AsNoTracking().SingleAsync(c => c.Id == b2cCriterion.Id)).CampaignId);
        Assert.Equal(campaignId, (await read.RubricCriteria.AsNoTracking().SingleAsync(c => c.Id == b2bCriterion.Id)).CampaignId);
    }
}
