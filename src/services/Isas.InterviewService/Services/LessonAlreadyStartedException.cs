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
