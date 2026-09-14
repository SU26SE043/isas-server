using Isas.Shared.Scoring;
using Xunit;

namespace Isas.Shared.Tests.Scoring;

/// <summary>SCP1-B1 — đánh giá: số học decimal, so sánh 1/0, hàm, <c>if()</c> LAZY, chia-0, biến lạ,
/// kết quả ngoài [0,100].</summary>
public class ScoringExpressionEvaluateTests
{
    private static readonly ScoringContext Iv = ScoringContext.Sample(ScoringExpressionKind.Interview);

    private static decimal Eval(string expr, ScoringContext? ctx = null)
    {
        var r = ScoringExpression.Parse(expr).Evaluate(ctx ?? Iv);
        Assert.True(r.Ok, $"kỳ vọng OK, nhận lỗi: {(r.Errors.Count > 0 ? r.Errors[0].Code : "?")}");
        return r.Value!.Value;
    }

    private static ScoringError EvalError(string expr, ScoringContext? ctx = null)
    {
        var r = ScoringExpression.Parse(expr).Evaluate(ctx ?? Iv);
        Assert.False(r.Ok);
        return Assert.Single(r.Errors);
    }

    // ── Số học ────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("2 + 3", "5")]
    [InlineData("10 - 4", "6")]
    [InlineData("6 * 7", "42")]
    [InlineData("20 / 8", "2.5")]         // decimal — KHÔNG phải 2 (int) hay 2.4999.. (double)
    [InlineData("9 / 4 + 0.75", "3")]     // 2.25 + 0.75
    [InlineData("0.1 + 0.2", "0.3")]      // decimal: đúng 0.3, không phải 0.30000000000000004
    public void SoHoc_Decimal(string expr, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Eval(expr));

    // ── So sánh trả 1/0 ──────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("5 > 3", "1")]
    [InlineData("5 < 3", "0")]
    [InlineData("5 >= 5", "1")]
    [InlineData("5 <= 4", "0")]
    [InlineData("5 == 5", "1")]
    [InlineData("5 == 6", "0")]
    [InlineData("5 != 6", "1")]
    [InlineData("5 != 5", "0")]
    public void SoSanh_TraMotHoacKhong(string expr, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Eval(expr));

    // ── Hàm biến thiên ──────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("min(10, 3, 7)", "3")]
    [InlineData("max(10, 3, 7)", "10")]
    [InlineData("min(5)", "5")]
    [InlineData("sum(10, 20, 30)", "60")]
    [InlineData("avg(10, 20, 30)", "20")]
    [InlineData("avg(10, 20, 30, 40)", "25")]
    public void Ham_BienThien(string expr, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Eval(expr));

    // ── round: nửa làm tròn RA XA số 0 (tất định) ───────────────────────────────────────────
    [Theory]
    [InlineData("round(2.4)", "2")]
    [InlineData("round(2.5)", "3")]
    [InlineData("round(2.6)", "3")]
    [InlineData("round(2.49)", "2")]
    public void Ham_Round(string expr, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Eval(expr));

    // ── clamp(x, lo, hi) — công cụ của NGƯỜI VIẾT để không dính RESULT_OUT_OF_RANGE ──────────
    [Theory]
    [InlineData("clamp(150, 0, 100)", "100")]
    [InlineData("clamp(0 - 10, 0, 100)", "0")]
    [InlineData("clamp(50, 0, 100)", "50")]
    public void Ham_Clamp(string expr, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Eval(expr));

    [Fact]
    public void Ham_CountBelow_DemTieuChiDuoiNguong()
    {
        // Bộ mẫu PHỎNG VẤN: pct = [80, 60, 40].
        Assert.Equal(1m, Eval("count_below(50)"));
        Assert.Equal(2m, Eval("count_below(70)"));
        Assert.Equal(0m, Eval("count_below(40)"));  // '<' nghiêm ngặt: 40 KHÔNG < 40
        Assert.Equal(3m, Eval("count_below(1000)"));
    }

    // ── if() LAZY ───────────────────────────────────────────────────────────────────────────
    [Fact]
    public void If_Lazy_NhanhKhongChon_CoChia0_KhongNem()
    {
        var cv0 = ScoringContext.ForCvScreening(new CvScreeningScoringInputs(0, 0, 0, 0, 0, 0));
        var r = ScoringExpression.Parse("if(need_count == 0, 0, 100 / need_count)").Evaluate(cv0);
        Assert.True(r.Ok);
        Assert.Equal(0m, r.Value);
    }

    [Fact]
    public void If_Lazy_NhanhTrue_KhongChon_CoChia0_KhongNem()
    {
        var r = ScoringExpression.Parse("if(1 == 0, 5 / 0, 42)").Evaluate(Iv);
        Assert.True(r.Ok);
        Assert.Equal(42m, r.Value);
    }

    [Fact]
    public void If_ChonNhanhSai_KhiCondBang0()
        => Assert.Equal(2m, Eval("if(5 > 10, 1, 2)"));

    [Fact]
    public void If_Long()
        => Assert.Equal(20m, Eval("if(1 == 1, if(0 == 1, 10, 20), 30)"));

    [Fact]
    public void If_CondKhac0_LaTrue()  // cond không nhất thiết là 0/1
        => Assert.Equal(7m, Eval("if(3, 7, 9)"));

    // ── DIVIDE_BY_ZERO khi nhánh ĐƯỢC chọn thật sự chia 0 ───────────────────────────────────
    [Fact]
    public void Chia0_TrenNhanhDuocChon_DungMa_DungViTri()
    {
        var e = EvalError("100 / need_count", ScoringContext.ForCvScreening(new(0, 0, 0, 0, 0, 0)));
        Assert.Equal(ScoringErrorCodes.DivideByZero, e.Code);
        Assert.Equal(0, e.Start);
        Assert.Equal("100 / need_count".Length, e.End);
    }

    [Fact]
    public void Chia0_HangSo()
    {
        var e = EvalError("10 / 0");
        Assert.Equal(ScoringErrorCodes.DivideByZero, e.Code);
        Assert.Equal(0, e.Start);
        Assert.Equal(6, e.End);
    }

    // ── UNKNOWN_VARIABLE (lúc chạy, cần context) + vị trí ───────────────────────────────────
    [Fact]
    public void BienLa_DungMa_DungViTri()
    {
        var e = EvalError("weighted_avg_pct + bogus");
        Assert.Equal(ScoringErrorCodes.UnknownVariable, e.Code);
        Assert.Equal("weighted_avg_pct + ".Length, e.Start);
        Assert.Equal("weighted_avg_pct + bogus".Length, e.End);
    }

    [Fact]
    public void BienLa_BienCuaLoaiKhac_VanLaLa()
    {
        // strong_count là biến SÀNG CV — không hợp lệ trong context PHỎNG VẤN.
        var e = EvalError("strong_count");
        Assert.Equal(ScoringErrorCodes.UnknownVariable, e.Code);
    }

    // ── RESULT_OUT_OF_RANGE — chỉ kết quả CUỐI, trung gian tự do ────────────────────────────
    [Fact]
    public void KetQuaCuoi_TrenTran_RaLoi_KemGiaTri()
    {
        var r = ScoringExpression.Parse("200").Evaluate(Iv);
        Assert.False(r.Ok);
        Assert.Equal(ScoringErrorCodes.ResultOutOfRange, Assert.Single(r.Errors).Code);
        Assert.Equal(200m, r.Value);   // trả giá trị ngoài dải để chẩn đoán
    }

    [Fact]
    public void KetQuaCuoi_DuoiKhong_RaLoi()
    {
        var r = ScoringExpression.Parse("0 - 50").Evaluate(Iv);
        Assert.False(r.Ok);
        Assert.Equal(ScoringErrorCodes.ResultOutOfRange, Assert.Single(r.Errors).Code);
        Assert.Equal(-50m, r.Value);
    }

    [Fact]
    public void TrungGian_NgoaiDai_NhungKetQuaCuoiTrongDai_OK()
        => Assert.Equal(50m, Eval("weighted_avg_pct * 2 - 82"));   // 66*2=132 (ngoài), -82 → 50 (trong)

    [Fact]
    public void Bien_10_Bien_100_LaBienGioiHopLe()
    {
        Assert.Equal(0m, Eval("min_pct * 0"));
        Assert.Equal(100m, Eval("clamp(max_pct + 1000, 0, 100)"));
    }

    [Fact]
    public void PhanTichMotLan_DanhGiaNhieuLan()
    {
        var parsed = ScoringExpression.Parse("weighted_avg_pct");
        var a = parsed.Evaluate(ScoringContext.ForInterview(new([new CriterionScore(50m, 1m)], 1, 1)));
        var b = parsed.Evaluate(ScoringContext.ForInterview(new([new CriterionScore(90m, 1m)], 1, 1)));
        Assert.Equal(50m, a.Value);
        Assert.Equal(90m, b.Value);
    }
}
