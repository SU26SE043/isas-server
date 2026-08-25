namespace Isas.InterviewService.Models;

// BC15 (D20) — cấu hình đánh giá roadmap theo level. Ngưỡng % đạt (passed = percentage ≥ ngưỡng).
// Ngưỡng CHỐT lúc build report (đổi config sau không hồi tố — snapshot vào roadmaps.final_report).
//
// MIS1-B6 — `ReinforceMinSessions` (từng ≥2 buổi, riêng cho mode Reinforce) đã GỠ khỏi đây: roadmap
// nay xây từ lỗi thật ở CẢ HAI mode, nên trần thật là "≥1 buổi đã chấm" — Guard 1 của
// `RoadmapService.CreateAsync` (ROADMAP_SESSIONS_REQUIRED) thay thế, không cần config riêng nữa.
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
    /// Số câu hỏi của buổi luyện khi bấm "Bắt đầu" một bài học roadmap.
    ///
    /// <para>Vì sao TĨNH, không adaptive: bài học đã có <c>focusCriteria</c> khoanh sẵn chủ đề nên
    /// giá trị của việc hỏi sâu/hỏi thêm thấp hơn hẳn buổi luyện tự do, trong khi số câu bập bênh
    /// (5 câu gốc + chuỗi đào sâu + câu bù tự động) làm người học không lường trước được thời lượng.</para>
    /// </summary>
    public int LessonQuestionCount { get; set; } = 5;

    /// <summary>
    /// Bật/tắt adaptive cho buổi luyện trong bài học roadmap. Mặc định TẮT.
    ///
    /// <para>⚠ KHÔNG dùng <c>Adaptive:MaxDeepPerQuestion=0</c> để tắt đào sâu ở đây — nó đổi CHẾ ĐỘ
    /// (frontier cũ) chứ không tắt, và <c>MaxFollowUps</c> quay lại 3 nên vẫn chèn thêm câu ở đuôi.
    /// Cờ này là kill-switch per-session thật (<c>PracticeService.ResolveAdaptive</c>).</para>
    /// </summary>
    public bool LessonAdaptiveEnabled { get; set; } = false;

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
