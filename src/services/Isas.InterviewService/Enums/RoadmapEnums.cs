namespace Isas.InterviewService.Enums;

// BC12 (D20) — enum roadmap ôn tập cá nhân hoá B2C. Lưu string trong DB (HasConversion<string>).

// Level ứng viên tự chọn khi tạo roadmap (ngưỡng đánh giá theo level tính ở BC15).
public enum RoadmapLevel
{
    Fresher,
    Junior,
    Middle,
    Senior
}

/// <summary>
/// Chế độ lộ trình — người học chọn lúc tạo, LƯU lại (khác <c>scope</c> vốn chỉ có nghĩa lúc tạo).
///
/// <para><see cref="LevelUp"/> = hành vi cũ: <c>roadmaps.level</c> là trình độ MỤC TIÊU, nội dung
/// sinh ra để đi lên một bậc.</para>
///
/// <para><see cref="Reinforce"/> = ôn lại: GIỮ NGUYÊN trình độ hiện tại, bám các điểm yếu ĐO ĐƯỢC
/// (<c>session_criterion_scores</c> + <c>answer_scores.reasoning</c>) và nghiêng về lý thuyết giải
/// thích *vì sao lần trước sai*. Cần buổi luyện đã chấm làm dữ liệu — xem
/// <c>RoadmapOptions.ReinforceMinSessions</c>.</para>
/// </summary>
public enum RoadmapMode
{
    LevelUp,      // mặc định — tiến lên cấp mục tiêu
    Reinforce     // ôn lại ở đúng trình độ hiện tại, vá chỗ hay sai
}

public enum RoadmapStatus
{
    Active,       // đang luyện
    Completed,    // hoàn tất mọi milestone → có report cuối (BC15)
    Abandoned     // bỏ dở
}

public enum MilestoneStatus
{
    Pending,      // chưa bắt đầu
    InProgress,   // đang luyện lesson trong milestone
    Completed     // xong → tính improvement (BC15)
}

public enum LessonStatus
{
    Theory,       // mới tạo — chưa/đang xem lý thuyết
    Practicing,   // đã /start session luyện (BC14)
    Done          // session luyện đã Scored
}
