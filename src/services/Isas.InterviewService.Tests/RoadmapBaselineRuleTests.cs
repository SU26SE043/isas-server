using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;

namespace Isas.InterviewService.Tests;

/// <summary>
/// 2026-10-04 — mốc ban đầu = trung bình các buổi đã chọn (đều theo BUỔI) và ba ô kết luận theo
/// luật của báo cáo lộ trình (khi chưa có kết luận AI).
/// </summary>
public class RoadmapBaselineRuleTests
{
    // Một buổi có HAI dòng cùng tên tiêu chí (rubric đổi version) không được nặng gấp đôi buổi khác:
    // A = (0+100)/2 = 50, B = 20 ⇒ mốc (50+20)/2 = 35 — trung bình theo DÒNG sẽ ra 40.
    [Fact]
    public void Average_DeuTheoBuoi_KhongTheoDong()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var baseline = RoadmapBaselineRule.Average(new[]
        {
            new RoadmapBaselineRule.Row(a, "Clarity", 0m),
            new RoadmapBaselineRule.Row(a, "Clarity", 100m),
            new RoadmapBaselineRule.Row(b, "Clarity", 20m),
            new RoadmapBaselineRule.Row(b, "Depth", 60m),
        });

        Assert.Equal(35m, baseline["Clarity"]);
        Assert.Equal(60m, baseline["Depth"]);
    }

    [Fact]
    public void Average_LamTron2ChuSo()
    {
        var baseline = RoadmapBaselineRule.Average(new[]
        {
            new RoadmapBaselineRule.Row(Guid.NewGuid(), "Clarity", 10m),
            new RoadmapBaselineRule.Row(Guid.NewGuid(), "Clarity", 20m),
            new RoadmapBaselineRule.Row(Guid.NewGuid(), "Clarity", 20m),
        });
        Assert.Equal(16.67m, baseline["Clarity"]);
    }

    private static RoadmapRadarCriterionResponse Radar(string name, decimal pct, decimal? start = null)
        => new(Guid.NewGuid(), name, 5m, 1m, pct, pct / 20m, start, start is null ? 1 : 2, start is null ? 1 : 2);

    // Đúng ca prod 2026-10-04 (ngưỡng Fresher 50): mọi tiêu chí chưa đạt; "tiến bộ" so mốc ban đầu,
    // tiêu chí tụt (Trôi chảy 60 → 45) KHÔNG vào danh sách tiến bộ.
    [Fact]
    public void RuleConclusions_ManhYeuTienBo_TheoNguongVaMoc()
    {
        var radar = new[]
        {
            Radar("Thuật ngữ", 44m), Radar("Trôi chảy", 45m), Radar("Thiết kế", 35m), Radar("Giao tiếp", 72m),
        };
        var baseline = new Dictionary<string, decimal>
        {
            ["Thuật ngữ"] = 10m, ["Trôi chảy"] = 60m, ["Thiết kế"] = 10m, ["Giao tiếp"] = 72m,
        };

        var (strengths, weaknesses, improvements) =
            RoadmapReportService.BuildRuleConclusions(radar, 50m, baseline);

        Assert.Equal(["Giao tiếp (72%)"], strengths);
        Assert.Equal(["Thiết kế (35%)", "Thuật ngữ (44%)", "Trôi chảy (45%)"], weaknesses);   // thấp trước
        Assert.Equal(["Thuật ngữ: 10% → 44% (+34)", "Thiết kế: 10% → 35% (+25)"], improvements);  // tăng nhiều trước
    }

    // Không có mốc ban đầu cho tiêu chí → dùng % buổi đầu tiên trong lộ trình (radar.StartPercentage);
    // không có cả hai → KHÔNG coi là tăng từ 0. Đúng ngưỡng = đạt (≥).
    [Fact]
    public void RuleConclusions_KhongCoBaseline_DungBuoiDau_KhongCoMocThiKhongXet()
    {
        var radar = new[]
        {
            Radar("Clarity", 80m, start: 40m),   // có buổi đầu
            Radar("Depth", 50m),                 // 1 buổi, không mốc
        };

        var (strengths, weaknesses, improvements) =
            RoadmapReportService.BuildRuleConclusions(radar, 50m, baseline: null);

        Assert.Equal(["Clarity (80%)", "Depth (50%)"], strengths);
        Assert.Empty(weaknesses);
        Assert.Equal(["Clarity: 40% → 80% (+40)"], improvements);
    }

    // Số in bằng InvariantCulture — máy chủ vi-VN không được in "44,5%".
    [Fact]
    public void RuleConclusions_SoThapPhan_KhongTheoLocaleMayChu()
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("vi-VN");
            var (_, weaknesses, _) = RoadmapReportService.BuildRuleConclusions(
                [Radar("Clarity", 44.5m)], 50m, null);
            Assert.Equal(["Clarity (44.5%)"], weaknesses);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }
}
