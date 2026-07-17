using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// DB15 — CHECK ck_rubric_criteria_weight_range: rubric_criteria.weight ∈ (0,1] (bound (0,1] khớp code:
/// RubricLibraryService chuẩn hoá Σweight=1 nên mỗi tiêu chí >0; seed BC11 mỗi tiêu chí ≤1). Chặn
/// dữ liệu bẩn ở tầng DB. TestDb dùng UseSnakeCaseNamingConvention → cột weight khớp SQL của CHECK.
/// </summary>
public class CriterionWeightRangeCheckTests
{
    private static RubricCriterion Crit(decimal weight)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Clarity",
            Description = "Trình bày rõ ràng",
            Weight = weight,
            MaxScore = 5,
            IsActive = true,
            JobCategory = JobCategory.BE,
            Version = 1
        };

    // Vi phạm: weight = 0 (≤0) → CHECK chặn.
    [Fact]
    public async Task WeightZero_Violates_Check()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(Crit(0m));

        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    // Vi phạm: weight > 1 → CHECK chặn.
    [Fact]
    public async Task WeightAboveOne_Violates_Check()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(Crit(1.5m));

        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    // Hợp lệ: weight ∈ (0,1] — biên (rất nhỏ >0, giữa, đúng 1).
    [Theory]
    [InlineData(0.0001)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task WeightInRange_Passes_Check(double weight)
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(Crit((decimal)weight));

        var ex = await Record.ExceptionAsync(() => t.Db.SaveChangesAsync());
        Assert.Null(ex);
    }
}
