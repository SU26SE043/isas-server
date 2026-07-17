using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// DB19 — CHECK ck_rubric_criteria_single_owner: rubric_criteria KHÔNG được set ĐỒNG THỜI
/// campaign_id (B2B) và candidate_id (B2C, BC16). 3 trạng thái loại trừ hợp lệ:
/// campaign-only · candidate-only · both-null (seed mặc định BC11).
/// </summary>
public class RubricSingleOwnerCheckTests
{
    private static RubricCriterion Crit(Guid? campaignId, Guid? candidateId)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Clarity",
            Description = "Trình bày rõ ràng",
            Weight = 1.0m,
            MaxScore = 5,
            IsActive = true,
            JobCategory = JobCategory.BE,
            Version = 1,
            CampaignId = campaignId,
            CandidateId = candidateId
        };

    // Vi phạm: cả 2 cùng set → CHECK chặn → DbUpdateException.
    [Fact]
    public async Task BothOwnersSet_Violates_Check()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(Crit(Guid.NewGuid(), Guid.NewGuid()));

        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    // Hợp lệ: campaign-only (B2B).
    [Fact]
    public async Task CampaignOnly_Passes_Check()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(Crit(Guid.NewGuid(), null));

        var ex = await Record.ExceptionAsync(() => t.Db.SaveChangesAsync());
        Assert.Null(ex);
    }

    // Hợp lệ: candidate-only (B2C rubric cá nhân, BC16).
    [Fact]
    public async Task CandidateOnly_Passes_Check()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(Crit(null, Guid.NewGuid()));

        var ex = await Record.ExceptionAsync(() => t.Db.SaveChangesAsync());
        Assert.Null(ex);
    }

    // Hợp lệ: both-null (seed mặc định dùng chung, BC11).
    [Fact]
    public async Task BothNull_Passes_Check()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(Crit(null, null));

        var ex = await Record.ExceptionAsync(() => t.Db.SaveChangesAsync());
        Assert.Null(ex);
    }
}
