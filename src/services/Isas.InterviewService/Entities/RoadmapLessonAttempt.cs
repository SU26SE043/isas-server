namespace Isas.InterviewService.Entities;

/// <summary>
/// Một LẦN LÀM một bài luyện trong lộ trình. Người học được luyện lại bài đã xong để nâng điểm
/// (mỗi lần tốn 1 credit, câu hỏi sinh mới) — bảng này giữ TRỌN lịch sử các lần đó.
///
/// <para><b>Vì sao cần bảng riêng thay vì ghi đè <see cref="RoadmapLesson.SessionId"/>:</b> cột đó
/// là quan hệ 1–1, làm lại mà ghi đè thì buổi cũ biến mất khỏi mọi đường đọc — đúng thứ báo cáo
/// tiến độ sinh ra để hiển thị. Bảng này ADDITIVE: <c>lesson.session_id</c> giữ nguyên ngữ nghĩa
/// "buổi MỚI NHẤT" nên mọi chỗ đang đọc nó (BC15 improvement, rollup Scored→Done, FE) không đổi
/// một dòng.</para>
///
/// <para><b>Bất biến:</b> <c>UNIQUE(lesson_id, attempt_no)</c> là lá chắn ở tầng DB cho việc cấp
/// số thứ tự — hai request làm lại cùng lúc thì một cái phải vỡ, không được im lặng cấp trùng số.
/// <c>UNIQUE(session_id)</c> giữ 1 buổi ↔ 1 lần làm.</para>
/// </summary>
public class RoadmapLessonAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LessonId { get; set; }
    public RoadmapLesson Lesson { get; set; } = null!;

    /// <summary>
    /// Buổi luyện của lần làm này. FK Restrict → practice_sessions (giữ lịch sử, không cho xoá buổi
    /// đang được một lần làm trỏ tới) — cùng ràng buộc như <see cref="RoadmapLesson.SessionId"/>.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>Đếm từ 1 theo từng lesson. Lần đầu (bấm Bắt đầu) = 1; mỗi lần làm lại +1.</summary>
    public int AttemptNo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
