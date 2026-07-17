using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Tests;

/// <summary>
/// DB15 — CHECK ck_campaigns_pass_score_pct_range: pass_score_pct (E5) phải NULL (HR quyết tay)
/// hoặc ∈ [0,100]. Khớp guard code ValidatePassScorePct. SQLite (snake_case TestDb) enforce CHECK
/// → vi phạm ném DbUpdateException (precedent DB19 RubricSingleOwnerCheckTests / DB1 Payment).
/// </summary>
public class PassScorePctRangeCheckTests
{
    private static Campaign CampaignWithPct(int? pct)
    {
        var c = CampaignTestDb.NewCampaign(Guid.NewGuid());
        c.PassScorePct = pct;
        return c;
    }

    // Vi phạm: âm → CHECK chặn.
    [Fact]
    public async Task Negative_Violates_Check()
    {
        using var tdb = new CampaignTestDb();
        tdb.Db.Campaigns.Add(CampaignWithPct(-1));

        await Assert.ThrowsAsync<DbUpdateException>(() => tdb.Db.SaveChangesAsync());
    }

    // Vi phạm: > 100 → CHECK chặn.
    [Fact]
    public async Task Over100_Violates_Check()
    {
        using var tdb = new CampaignTestDb();
        tdb.Db.Campaigns.Add(CampaignWithPct(101));

        await Assert.ThrowsAsync<DbUpdateException>(() => tdb.Db.SaveChangesAsync());
    }

    // Hợp lệ: NULL (HR quyết tay) / biên 0 / biên 100.
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(100)]
    public async Task NullOrInRange_Passes_Check(int? pct)
    {
        using var tdb = new CampaignTestDb();
        tdb.Db.Campaigns.Add(CampaignWithPct(pct));

        var ex = await Record.ExceptionAsync(() => tdb.Db.SaveChangesAsync());
        Assert.Null(ex);
    }
}

/// <summary>
/// DB15 — CHECK ck_campaign_criteria_weight_range: weight ∈ (0,1] (khớp guard code
/// BuildStructuredCriteria: 0 &lt; weight ≤ 1). Criterion cần parent Campaign (FK enforce trên
/// SQLite EF10 — bài học DB9).
/// </summary>
public class CriterionWeightRangeCheckTests
{
    // Seed 1 campaign (parent FK) + 1 criterion với weight cho trước, SaveChanges.
    private static async Task<Exception?> InsertCriterionAsync(CampaignTestDb tdb, decimal weight)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid());
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        tdb.Db.CampaignCriteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            OrderNo = 0,
            Name = "Crit",
            Weight = weight,
            MaxScore = 5,
            Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return await Record.ExceptionAsync(() => tdb.Db.SaveChangesAsync());
    }

    // Vi phạm: weight = 0 (không > 0) → CHECK chặn.
    [Fact]
    public async Task Zero_Violates_Check()
    {
        using var tdb = new CampaignTestDb();
        var ex = await InsertCriterionAsync(tdb, 0m);

        Assert.IsType<DbUpdateException>(ex);
    }

    // Vi phạm: weight = 1.5 (> 1) → CHECK chặn.
    [Fact]
    public async Task Over1_Violates_Check()
    {
        using var tdb = new CampaignTestDb();
        var ex = await InsertCriterionAsync(tdb, 1.5m);

        Assert.IsType<DbUpdateException>(ex);
    }

    // Hợp lệ: weight ∈ (0,1] — 0.5 và biên 1.0.
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task InRange_Passes_Check(double weight)
    {
        using var tdb = new CampaignTestDb();
        var ex = await InsertCriterionAsync(tdb, (decimal)weight);

        Assert.Null(ex);
    }
}
