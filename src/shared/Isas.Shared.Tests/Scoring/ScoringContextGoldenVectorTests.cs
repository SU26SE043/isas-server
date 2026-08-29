using Isas.Shared.Scoring;
using Xunit;

namespace Isas.Shared.Tests.Scoring;

/// <summary>
/// SCP1-B1 · HĐ-1 — GOLDEN VECTOR cho TỪNG BIẾN của cả hai loại. Input CỐ ĐỊNH → giá trị CỐ ĐỊNH.
///
/// <para>Đây là thứ DUY NHẤT chặn việc đổi nghĩa một biến về sau: sửa cách suy biến trong
/// <see cref="ScoringContext"/> mà không làm đỏ test nào ở đây nghĩa là golden vector chưa phủ hết —
/// THÊM vector, đừng nới assert.</para>
/// </summary>
public class ScoringContextGoldenVectorTests
{
    // Buổi PHỎNG VẤN mẫu — chọn số chia hết để giá trị kỳ vọng là số nguyên/thập phân gọn:
    //   tiêu chí: pct 80 (w .5) · 60 (w .3) · 40 (w .2)   → Σw = 1.0
    //   trả lời 8 / tổng 10 câu
    private static ScoringContext Interview() => ScoringContext.ForInterview(new InterviewScoringInputs(
        Criteria:
        [
            new CriterionScore(80m, 0.5m),
            new CriterionScore(60m, 0.3m),
            new CriterionScore(40m, 0.2m),
        ],
        Answered: 8,
        TotalQuestions: 10));

    [Theory]
    [InlineData("weighted_avg_pct", "66")]   // 80*.5 + 60*.3 + 40*.2 = 40 + 18 + 8, chia Σw 1.0
    [InlineData("avg_pct", "60")]            // (80 + 60 + 40) / 3
    [InlineData("min_pct", "40")]
    [InlineData("max_pct", "80")]
    [InlineData("answered", "8")]
    [InlineData("total_questions", "10")]
    [InlineData("completeness", "0.8")]      // 8 / 10 — PHÂN SỐ, không nhân 100
    public void GoldenVector_PhongVan(string variable, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Interview().Variables[variable]);

    [Fact]
    public void GoldenVector_PhongVan_DuTapBien()
        => Assert.Equal(
            ScoringVariableCatalog.Interview.OrderBy(x => x, StringComparer.Ordinal),
            Interview().Variables.Keys.OrderBy(x => x, StringComparer.Ordinal));

    [Fact]
    public void GoldenVector_PhongVan_CountBelowSeries_LaDanhSachPct()
    {
        var ctx = Interview();
        Assert.Equal(1m, Count(ctx, 50m));   // chỉ 40
        Assert.Equal(2m, Count(ctx, 61m));   // 40, 60
        Assert.Equal(3m, Count(ctx, 81m));   // cả 3
        Assert.Equal(0m, Count(ctx, 40m));   // '<' nghiêm ngặt
    }

    [Fact]
    public void GoldenVector_PhongVan_RubricRong_TatCaAggregateVe0()
    {
        var ctx = ScoringContext.ForInterview(new InterviewScoringInputs([], Answered: 0, TotalQuestions: 0));
        Assert.Equal(0m, ctx.Variables["weighted_avg_pct"]);
        Assert.Equal(0m, ctx.Variables["avg_pct"]);
        Assert.Equal(0m, ctx.Variables["min_pct"]);
        Assert.Equal(0m, ctx.Variables["max_pct"]);
        Assert.Equal(0m, ctx.Variables["completeness"]);   // chia 0 câu → 0, KHÔNG ném
        Assert.Empty(ctx.CountBelowSeries);
    }

    [Fact]
    public void GoldenVector_PhongVan_MotTieuChi()
    {
        var ctx = ScoringContext.ForInterview(new InterviewScoringInputs(
            [new CriterionScore(73m, 1m)], Answered: 3, TotalQuestions: 4));
        Assert.Equal(73m, ctx.Variables["weighted_avg_pct"]);
        Assert.Equal(73m, ctx.Variables["avg_pct"]);
        Assert.Equal(73m, ctx.Variables["min_pct"]);
        Assert.Equal(73m, ctx.Variables["max_pct"]);
        Assert.Equal(0.75m, ctx.Variables["completeness"]);
    }

    // ── SÀNG CV ────────────────────────────────────────────────────────────────────────────────
    //   strong 3 · partial 2 · weak 1 · need 6 · must_have_total 4 · must_have_met 3
    private static ScoringContext Cv() => ScoringContext.ForCvScreening(new CvScreeningScoringInputs(
        StrongCount: 3, PartialCount: 2, WeakCount: 1, NeedCount: 6, MustHaveTotal: 4, MustHaveMet: 3));

    [Theory]
    [InlineData("strong_count", "3")]
    [InlineData("partial_count", "2")]
    [InlineData("weak_count", "1")]
    [InlineData("need_count", "6")]
    [InlineData("must_have_total", "4")]
    [InlineData("must_have_met", "3")]
    public void GoldenVector_SangCv(string variable, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Cv().Variables[variable]);

    [Fact]
    public void GoldenVector_SangCv_DuTapBien()
        => Assert.Equal(
            ScoringVariableCatalog.CvScreening.OrderBy(x => x, StringComparer.Ordinal),
            Cv().Variables.Keys.OrderBy(x => x, StringComparer.Ordinal));

    [Fact]
    public void GoldenVector_SangCv_CountBelowSeries_QuyDoi_100_50_0()
    {
        // Chuỗi = [0]·weak(1) ++ [50]·partial(2) ++ [100]·strong(3).
        var ctx = Cv();
        Assert.Equal(6, ctx.CountBelowSeries.Count);
        Assert.Equal(0m, Count(ctx, 0m));     // '<' nghiêm ngặt
        Assert.Equal(1m, Count(ctx, 1m));     // chỉ weak (0)
        Assert.Equal(1m, Count(ctx, 50m));    // chỉ weak
        Assert.Equal(3m, Count(ctx, 51m));    // weak + 2 partial
        Assert.Equal(3m, Count(ctx, 100m));   // weak + 2 partial (strong = 100 KHÔNG < 100)
        Assert.Equal(6m, Count(ctx, 101m));   // tất cả
    }

    [Fact]
    public void GoldenVector_SangCv_KhongCoNhuCau_ChuoiRong()
    {
        var ctx = ScoringContext.ForCvScreening(new CvScreeningScoringInputs(0, 0, 0, 0, 0, 0));
        Assert.Empty(ctx.CountBelowSeries);
        Assert.Equal(0m, ctx.Variables["need_count"]);
    }

    // ── Bộ MẪU của validate (HĐ-2) ────────────────────────────────────────────────────────────
    [Fact]
    public void Sample_PhongVan_KhopGiaTriKyVong()
    {
        var s = ScoringContext.Sample(ScoringExpressionKind.Interview);
        Assert.Equal(ScoringExpressionKind.Interview, s.Kind);
        Assert.Equal(66m, s.Variables["weighted_avg_pct"]);   // pct 80(.5) 60(.3) 40(.2)
        Assert.Equal(7, s.Variables.Count);
    }

    [Fact]
    public void Sample_SangCv_KhopGiaTriKyVong()
    {
        var s = ScoringContext.Sample(ScoringExpressionKind.CvScreening);
        Assert.Equal(ScoringExpressionKind.CvScreening, s.Kind);
        Assert.Equal(3m, s.Variables["strong_count"]);
        Assert.Equal(6m, s.Variables["need_count"]);
        Assert.Equal(6, s.Variables.Count);
    }

    [Fact]
    public void Sample_KhongTaoChia0TuNhien()
    {
        // Mọi mẫu số "tự nhiên" (need_count, total_questions) > 0 trong bộ mẫu.
        Assert.True(ScoringContext.Sample(ScoringExpressionKind.CvScreening).Variables["need_count"] > 0m);
        Assert.True(ScoringContext.Sample(ScoringExpressionKind.Interview).Variables["total_questions"] > 0m);
    }

    private static decimal Count(ScoringContext ctx, decimal threshold)
        => ScoringExpression
            .Parse($"count_below({threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)})")
            .Evaluate(ctx).Value!.Value;
}
