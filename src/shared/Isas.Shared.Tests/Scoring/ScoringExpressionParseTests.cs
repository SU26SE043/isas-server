using Isas.Shared.Scoring;
using Xunit;

namespace Isas.Shared.Tests.Scoring;

/// <summary>SCP1-B1 — phân tích: ưu tiên toán tử, kết hợp, lỗi cú pháp/hàm/arity + VỊ TRÍ, 4 trần cứng.</summary>
public class ScoringExpressionParseTests
{
    private static ScoringError SingleError(string expr)
    {
        var r = ScoringExpression.Parse(expr);
        Assert.False(r.Ok);
        return Assert.Single(r.Errors);
    }

    // ── Ưu tiên toán tử (nhân/chia trước cộng/trừ, so sánh cuối) ────────────────────────────────
    [Fact]
    public void UuTien_2cong3nhan4_bang14()
    {
        // (2 + (3*4)) == 14  → true (1)
        var r = ScoringExpression.Parse("2 + 3 * 4 == 14").Evaluate(ScoringContext.Sample(ScoringExpressionKind.Interview));
        Assert.True(r.Ok);
        Assert.Equal(1m, r.Value);
    }

    [Fact]
    public void UuTien_NgoacDoiThuTu()
    {
        var r = ScoringExpression.Parse("(2 + 3) * 4 == 20").Evaluate(ScoringContext.Sample(ScoringExpressionKind.Interview));
        Assert.True(r.Ok);
        Assert.Equal(1m, r.Value);
    }

    [Theory]
    [InlineData("10 - 3 - 2", "5")]     // trái sang phải: (10-3)-2, KHÔNG phải 10-(3-2)=9
    [InlineData("100 / 4 / 5", "5")]    // (100/4)/5 = 5, KHÔNG phải 100/(4/5)=125
    [InlineData("2 * 3 + 4", "10")]
    [InlineData("4 + 2 * 3", "10")]
    [InlineData("0 - -5", "5")]         // trừ rồi phủ định một ngôi
    [InlineData("- -5 + 0", "5")]       // phủ định lồng
    public void KetHop_TraiSangPhai(string expr, string expected)
    {
        var r = ScoringExpression.Parse(expr).Evaluate(ScoringContext.Sample(ScoringExpressionKind.Interview));
        Assert.True(r.Ok);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), r.Value);
    }

    [Fact]
    public void KhoangTrang_VaXuongDong_BoQua()
    {
        var r = ScoringExpression.Parse("  weighted_avg_pct  \n ").Evaluate(ScoringContext.Sample(ScoringExpressionKind.Interview));
        Assert.True(r.Ok);
        Assert.Equal(66m, r.Value);
    }

    [Fact]
    public void So_ThapPhan_HopLe()
    {
        var r = ScoringExpression.Parse("73.5").Evaluate(ScoringContext.Sample(ScoringExpressionKind.Interview));
        Assert.True(r.Ok);
        Assert.Equal(73.5m, r.Value);
    }

    [Fact]
    public void ReferencedVariables_DistinctVaSapOrdinal()
    {
        var r = ScoringExpression.Parse("if(min_pct < 40, weighted_avg_pct * 0.8, weighted_avg_pct)");
        Assert.True(r.Ok);
        Assert.Equal(new[] { "min_pct", "weighted_avg_pct" }, r.ReferencedVariables);
    }

    [Fact]
    public void ReferencedVariables_HangSo_Rong()
        => Assert.Empty(ScoringExpression.Parse("round(100)").ReferencedVariables);

    // ── SYNTAX_ERROR + vị trí ──────────────────────────────────────────────────────────────────
    [Fact]
    public void CuPhap_KyTuLa_DungViTri()
    {
        var e = SingleError("1 % 2");
        Assert.Equal(ScoringErrorCodes.SyntaxError, e.Code);
        Assert.Equal(2, e.Start);
        Assert.Equal(3, e.End);
    }

    [Theory]
    [InlineData("1 & 2")]
    [InlineData("a | b")]
    [InlineData("2 ^ 3")]
    [InlineData("criteria[0]")]   // cú pháp truy cập mảng — CẤM, phải gãy
    [InlineData("needs where x")]
    public void CuPhap_MuuToanMoNgonNguTruyVan_Gay(string expr)
        => Assert.Equal(ScoringErrorCodes.SyntaxError, SingleError(expr).Code);

    [Fact]
    public void CuPhap_TokenThua()
    {
        var e = SingleError("1 2");
        Assert.Equal(ScoringErrorCodes.SyntaxError, e.Code);
        Assert.Equal(2, e.Start);
        Assert.Equal(3, e.End);
    }

    [Fact]
    public void CuPhap_ThieuNgoacDong()
        => Assert.Equal(ScoringErrorCodes.SyntaxError, SingleError("(1 + 2").Code);

    [Fact]
    public void CuPhap_BieuThucRong()
        => Assert.Equal(ScoringErrorCodes.SyntaxError, SingleError("").Code);

    [Fact]
    public void CuPhap_ToanTuThieuVeThua()
        => Assert.Equal(ScoringErrorCodes.SyntaxError, SingleError("1 +").Code);

    [Fact]
    public void CuPhap_So_ChamKhongCoChuSoSau()
        => Assert.Equal(ScoringErrorCodes.SyntaxError, SingleError("1. + 2").Code);

    [Fact]
    public void CuPhap_ChuoiSoSanh_ChenGiua_KhongGay()
    {
        // '<' '=' liền nhau là '<=' — không phải hai token; chỉ kiểm không gãy phân tích.
        var r = ScoringExpression.Parse("min_pct <= 40");
        Assert.True(r.Ok);
    }

    // ── UNKNOWN_FUNCTION + vị trí (chỉ vào TÊN hàm) ────────────────────────────────────────────
    [Fact]
    public void HamLa_DungMa_DungViTri_TaiVietNhap()
    {
        var e = SingleError("foobar(1)");
        Assert.Equal(ScoringErrorCodes.UnknownFunction, e.Code);
        Assert.Equal(0, e.Start);
        Assert.Equal(6, e.End);
    }

    [Fact]
    public void HamLa_TrongBieuThuc_ViTriTen()
    {
        var e = SingleError("1 + baz(2)");
        Assert.Equal(ScoringErrorCodes.UnknownFunction, e.Code);
        Assert.Equal(4, e.Start);
        Assert.Equal(7, e.End);
    }

    // ── WRONG_ARG_COUNT + vị trí ──────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("clamp(1, 2)")]        // clamp cần đúng 3
    [InlineData("if(1, 2)")]           // if cần đúng 3
    [InlineData("round(1, 2)")]        // round cần đúng 1
    [InlineData("min()")]              // min cần ≥ 1
    [InlineData("count_below(1, 2)")]  // count_below cần đúng 1
    [InlineData("clamp(1, 2, 3, 4)")]
    public void SaiSoThamSo_DungMa(string expr)
        => Assert.Equal(ScoringErrorCodes.WrongArgCount, SingleError(expr).Code);

    [Fact]
    public void SaiSoThamSo_ViTri_PhuTuTenDenNgoacDong()
    {
        var e = SingleError("clamp(1, 2)");
        Assert.Equal(ScoringErrorCodes.WrongArgCount, e.Code);
        Assert.Equal(0, e.Start);
        Assert.Equal("clamp(1, 2)".Length, e.End);
    }

    // ── 4 TRẦN CỨNG ───────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Tran_DoDaiBieuThuc_TooLong()
    {
        var e = SingleError(new string('9', ScoringLimits.MaxExpressionLength + 1));
        Assert.Equal(ScoringErrorCodes.TooLong, e.Code);
        Assert.Equal(0, e.Start);
        Assert.Equal(ScoringLimits.MaxExpressionLength + 1, e.End);
    }

    [Fact]
    public void Tran_SoNodeCay_TooManyNodes()
    {
        // 101 số + 100 dấu '+' = 201 node > 200.
        var expr = string.Join("+", System.Linq.Enumerable.Repeat("1", 101));
        Assert.Equal(ScoringErrorCodes.TooManyNodes, SingleError(expr).Code);
    }

    [Fact]
    public void Tran_DoSauLong_TooDeep()
    {
        var expr = new string('(', 40) + "1" + new string(')', 40);
        Assert.Equal(ScoringErrorCodes.TooDeep, SingleError(expr).Code);
    }

    [Fact]
    public void Tran_SoThamSoHam_WrongArgCount()
    {
        var expr = "min(" + string.Join(",", System.Linq.Enumerable.Repeat("1", ScoringLimits.MaxCallArguments + 1)) + ")";
        Assert.Equal(ScoringErrorCodes.WrongArgCount, SingleError(expr).Code);
    }

    [Fact]
    public void Tran_DuoiNguong_KhongGay()
    {
        // Sát trần nhưng KHÔNG vượt: min với đúng MaxCallArguments tham số phân tích được.
        var expr = "min(" + string.Join(",", System.Linq.Enumerable.Repeat("1", ScoringLimits.MaxCallArguments)) + ")";
        Assert.True(ScoringExpression.Parse(expr).Ok);
    }
}
