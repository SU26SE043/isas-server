namespace Isas.InterviewService.Models;

// BC15 (D20) — cấu hình đánh giá roadmap theo level. Ngưỡng % đạt (passed = percentage ≥ ngưỡng).
// Ngưỡng CHỐT lúc build report (đổi config sau không hồi tố — snapshot vào roadmaps.final_report).
public class RoadmapOptions
{
    public const string SectionName = "Roadmap";

    // Ngưỡng đạt theo level (mặc định Fresher 50 · Junior 60 · Middle 70 · Senior 80).
    // Key = tên level (RoadmapLevel.ToString()); override qua config Roadmap:LevelThresholdPct:<Level>.
    public Dictionary<string, int> LevelThresholdPct { get; set; } = new()
    {
        ["Fresher"] = 50,
        ["Junior"] = 60,
        ["Middle"] = 70,
        ["Senior"] = 80
    };

    // Ngưỡng cho 1 level — fallback về mặc định nếu config thiếu key (không vỡ khi cấu hình chưa đủ).
    public int ThresholdFor(string level) =>
        LevelThresholdPct.TryGetValue(level, out var pct)
            ? pct
            : level switch
            {
                "Fresher" => 50,
                "Junior" => 60,
                "Middle" => 70,
                "Senior" => 80,
                _ => 50
            };
}
