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
