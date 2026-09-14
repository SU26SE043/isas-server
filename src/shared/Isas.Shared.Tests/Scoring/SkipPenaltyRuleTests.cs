using Isas.Shared.Scoring;
using Xunit;

namespace Isas.Shared.Tests.Scoring;

/// <summary>
/// RNK1 · HĐ-2 / CAMP-21 — <see cref="SkipPenaltyRule.Apply"/>: một hàm dùng chung cho đường chấm
/// LIVE (InterviewService) và xem trước / áp (CampaignService). Khoá byte-equal + các ca không phạt.
/// </summary>
public class SkipPenaltyRuleTests
{
    private static InterviewScoringInputs In(bool? skipPenalty, int? seedAnswered, int? seedTotal)
        => new([new CriterionScore(70m, 1m)], Answered: 5, TotalQuestions: 8,
               SeedAnswered: seedAnswered, SeedTotal: seedTotal, SkipPenalty: skipPenalty);

    [Theory]
    [InlineData(80, 3, 5, 48)]      // 80 × 3/5
    [InlineData(66, 4, 8, 33)]      // 66 × 0.5
    [InlineData(90, 5, 5, 90)]      // trả lời hết câu gốc ⇒ không giảm
    [InlineData(100, 0, 5, 0)]      // bỏ hết câu gốc ⇒ 0
    public void Apply_PhatDung_KhiSkipPenaltyTrue(decimal expr, int seedAnswered, int seedTotal, decimal expected)
        => Assert.Equal(expected, SkipPenaltyRule.Apply(expr, In(true, seedAnswered, seedTotal)));

    [Fact]
    public void Apply_Clamp_KhongVuot100()
        => Assert.Equal(100m, SkipPenaltyRule.Apply(200m, In(true, 5, 5)));

    [Fact]
    public void Apply_Lam_Tron_2_Chu_So()
        // 66 × 4/7 = 37.714285… → 37.71
        => Assert.Equal(37.71m, SkipPenaltyRule.Apply(66m, In(true, 4, 7)));

    [Fact]
    public void Apply_KhongPhat_KhiSkipPenaltyFalse()
        => Assert.Equal(66m, SkipPenaltyRule.Apply(66m, In(false, 3, 5)));

    [Fact]
    public void Apply_KhongPhat_KhiSkipPenaltyNull_SnapshotTruocRnk1()
        => Assert.Equal(66m, SkipPenaltyRule.Apply(66m, In(null, null, null)));

    [Fact]
    public void Apply_KhongPhat_KhiSeedTotal0_KhongNemChia0()
        => Assert.Equal(66m, SkipPenaltyRule.Apply(66m, In(true, 0, 0)));

    [Fact]
    public void Apply_KhongPhat_KhiSeedTotalNull_ChiCoSkipPenaltyTrue()
        => Assert.Equal(66m, SkipPenaltyRule.Apply(66m, In(true, null, null)));

    [Fact]
    public void Apply_Nem_KhiInputNull()
        => Assert.Throws<System.ArgumentNullException>(() => SkipPenaltyRule.Apply(66m, null!));

    // Chặng dây HAY RỤNG (HĐ-1): ToInterviewInputs PHẢI mang đủ 6 trường xuống bộ đánh giá — nếu
    // rụng seed*/skipPenalty thì luật câu bỏ trống im lặng vô hiệu ở đường preview/apply.
    [Fact]
    public void ToInterviewInputs_MangDu6Truong()
    {
        var snap = new ScoringInputsSnapshot(
            [new CriterionInputSnapshot("A", 70m, 1m, 5, CriterionId: System.Guid.NewGuid())],
            Answered: 5, TotalQuestions: 8, SeedAnswered: 3, SeedTotal: 5, SkipPenalty: true);

        var ii = snap.ToInterviewInputs();

        Assert.Equal(5, ii.Answered);
        Assert.Equal(8, ii.TotalQuestions);
        Assert.Equal(3, ii.SeedAnswered);
        Assert.Equal(5, ii.SeedTotal);
        Assert.True(ii.SkipPenalty);
        Assert.Single(ii.Criteria);
        Assert.Equal(70m, ii.Criteria[0].Pct);
        Assert.Equal(1m, ii.Criteria[0].Weight);

        // Byte-equal: cùng snapshot ⇒ cùng điểm sau khi Apply, bất kể ai gọi.
        Assert.Equal(
            SkipPenaltyRule.Apply(80m, ii),
            SkipPenaltyRule.Apply(80m, snap.ToInterviewInputs()));
    }

    // Snapshot ghi TRƯỚC RNK1 (jsonb thiếu khoá seed*) ⇒ deserialize ra null ⇒ ToInterviewInputs
    // truyền null ⇒ không phạt.
    [Fact]
    public void ToInterviewInputs_SnapshotCu_SeedNull_KhongPhat()
    {
        var oldSnap = new ScoringInputsSnapshot(
            [new CriterionInputSnapshot("A", 70m, 1m, 5)], Answered: 3, TotalQuestions: 4);

        Assert.Null(oldSnap.SeedAnswered);
        Assert.Null(oldSnap.SeedTotal);
        Assert.Null(oldSnap.SkipPenalty);
        Assert.Equal(66m, SkipPenaltyRule.Apply(66m, oldSnap.ToInterviewInputs()));
    }
}
