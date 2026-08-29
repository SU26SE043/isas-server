using Isas.Shared.Scoring;
using Xunit;

namespace Isas.Shared.Tests.Scoring;

/// <summary>
/// SCP1-B1 · HĐ-1 — KHOÁ danh sách biến/hàm ĐÃ CÔNG BỐ. Nếu test này đỏ vì ai đó ĐỔI hoặc GỠ một
/// tên: dừng lại. Chỉ được THÊM tên mới ở cuối (và cập nhật assert này bằng cách nối thêm, không sửa
/// phần tử cũ).
/// </summary>
public class ScoringVariableCatalogTests
{
    [Fact]
    public void PhongVan_DanhSachBien_CoDinh()
        => Assert.Equal(
            new[] { "weighted_avg_pct", "avg_pct", "min_pct", "max_pct", "answered", "total_questions", "completeness" },
            ScoringVariableCatalog.Interview);

    [Fact]
    public void SangCv_DanhSachBien_CoDinh()
        => Assert.Equal(
            new[] { "strong_count", "partial_count", "weak_count", "need_count", "must_have_total", "must_have_met" },
            ScoringVariableCatalog.CvScreening);

    [Fact]
    public void DanhSachHam_CoDinh()
        => Assert.Equal(
            new[] { "min", "max", "avg", "sum", "round", "clamp", "if", "count_below" },
            ScoringVariableCatalog.Functions);

    [Fact]
    public void For_TraDungTapTheoLoai()
    {
        Assert.Equal(ScoringVariableCatalog.Interview, ScoringVariableCatalog.For(ScoringExpressionKind.Interview));
        Assert.Equal(ScoringVariableCatalog.CvScreening, ScoringVariableCatalog.For(ScoringExpressionKind.CvScreening));
    }

    [Fact]
    public void KhongTrungTenGiuaBienVaHam()
    {
        var all = ScoringVariableCatalog.Interview
            .Concat(ScoringVariableCatalog.CvScreening)
            .Concat(ScoringVariableCatalog.Functions)
            .ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}

/// <summary>SCP1-B1 — <see cref="ScoringExpression.Validate"/> (tiện ích HĐ-2): chạy thử trên bộ MẪU,
/// trả <c>sampleScore</c> hoặc danh sách MÃ lỗi.</summary>
public class ScoringExpressionValidateTests
{
    [Fact]
    public void Validate_MacDinh_PhongVan_TraSampleScore()
    {
        var r = ScoringExpression.Validate(ScoringExpressionKind.Interview, "weighted_avg_pct");
        Assert.True(r.Valid);
        Assert.Equal(66m, r.SampleScore);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Validate_SeedCvScreening_TraSampleScore()
    {
        // Template seed "Tỷ lệ nhu cầu đạt": round(100 * (strong + partial*0.5) / need)
        // Mẫu CV: strong 3, partial 2, need 6 → 100*(3 + 1)/6 = 66.666… → round → 67.
        var r = ScoringExpression.Validate(
            ScoringExpressionKind.CvScreening,
            "round(100 * (strong_count + partial_count * 0.5) / need_count)");
        Assert.True(r.Valid);
        Assert.Equal(67m, r.SampleScore);
    }

    [Fact]
    public void Validate_BienLa_KhongValid_MaUnknownVariable()
    {
        var r = ScoringExpression.Validate(ScoringExpressionKind.Interview, "bogus + 1");
        Assert.False(r.Valid);
        Assert.Null(r.SampleScore);
        Assert.Equal(ScoringErrorCodes.UnknownVariable, Assert.Single(r.Errors).Code);
    }

    [Fact]
    public void Validate_CuPhapHong_KhongValid_MaSyntaxError()
    {
        var r = ScoringExpression.Validate(ScoringExpressionKind.Interview, "weighted_avg_pct +");
        Assert.False(r.Valid);
        Assert.Equal(ScoringErrorCodes.SyntaxError, Assert.Single(r.Errors).Code);
    }

    [Fact]
    public void Validate_KetQuaNgoaiDai_KhongValid_MaResultOutOfRange()
    {
        var r = ScoringExpression.Validate(ScoringExpressionKind.Interview, "weighted_avg_pct + 100");
        Assert.False(r.Valid);
        Assert.Equal(ScoringErrorCodes.ResultOutOfRange, Assert.Single(r.Errors).Code);
    }

    [Fact]
    public void Validate_SeedChanDiemLiet_HopLe()
    {
        // Template seed "Chặn điểm liệt".
        var r = ScoringExpression.Validate(
            ScoringExpressionKind.Interview,
            "if(min_pct < 40, weighted_avg_pct * 0.8, weighted_avg_pct)");
        Assert.True(r.Valid);
        // Mẫu: min_pct = 40 (KHÔNG < 40) → nhánh else → weighted_avg_pct = 66.
        Assert.Equal(66m, r.SampleScore);
    }
}
