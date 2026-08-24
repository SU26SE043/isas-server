namespace Isas.InterviewService.Services;

// BC14 — lesson đang Practicing/Done khi gọi /start → 409 (resume session cũ thay vì tạo mới,
// KHÔNG reserve thêm credit). Mang theo session_id hiện có (nếu đang Practicing) để client resume.
public class LessonAlreadyStartedException : Exception
{
    public Guid? SessionId { get; }

    public LessonAlreadyStartedException(string message, Guid? sessionId)
        : base(message)
    {
        SessionId = sessionId;
    }
}

/// <summary>
/// Bài luyện KHÔNG ở trạng thái làm lại được → 409. Hai ca:
///   • còn <c>Theory</c> — chưa học lần nào thì bấm "Bắt đầu", không phải "Làm lại";
///   • đang <c>Practicing</c> — đang có buổi dở, phải tiếp tục buổi đó (kèm <see cref="SessionId"/>).
///
/// Tách khỏi <see cref="LessonAlreadyStartedException"/> vì cái tên đó nói "đã bắt đầu rồi", sai
/// nghĩa với ca <c>Theory</c> — và hai đường (Bắt đầu / Làm lại) có tiền điều kiện NGƯỢC nhau.
/// </summary>
public class LessonRetryNotAllowedException : Exception
{
    public Guid? SessionId { get; }

    public LessonRetryNotAllowedException(string message, Guid? sessionId)
        : base(message)
    {
        SessionId = sessionId;
    }
}
