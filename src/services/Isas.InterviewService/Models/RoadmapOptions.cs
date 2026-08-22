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

    /// <summary>
    /// Số buổi luyện ĐÃ CHẤM tối thiểu để được tạo lộ trình chế độ <c>Reinforce</c>.
    ///
    /// <para>Vì sao <b>2</b>: chế độ ôn tập bán lời hứa "vá chỗ bạn HAY sai". Một buổi duy nhất
    /// không phân biệt được "hay sai" với "hôm đó làm tệ" — không có tín hiệu LẶP LẠI nào, mà
    /// lặp lại chính là thứ khiến việc ôn có nghĩa. 2 là số NHỎ NHẤT cho thấy sự lặp.</para>
    ///
    /// <para>Vì sao KHÔNG phải 3: đo trên production chỉ có <b>4 người</b> đạt ≥3 buổi đã chấm —
    /// đặt 3 là tính năng chết ngay lúc ra mắt. Ngưỡng để ở config để nâng dần khi lượng dữ liệu
    /// lớn lên, thay vì phải sửa code.</para>
    ///
    /// <para><c>0</c> = tắt ngưỡng này. ⚠ KHÔNG tắt được guard "phải có điểm yếu": không có tiêu
    /// chí nào cần cải thiện thì chế độ ôn tập không có gì để ôn — bất khả thi theo cấu trúc, chứ
    /// không phải một mức chất lượng có thể hạ xuống.</para>
    /// </summary>
    public int ReinforceMinSessions { get; set; } = 2;

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
