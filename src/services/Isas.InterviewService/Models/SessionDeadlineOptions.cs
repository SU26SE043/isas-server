namespace Isas.InterviewService.Models;

/// <summary>
/// ATT1 — ân hạn sau hạn chót nhận bài của buổi (<c>practice_sessions.deadline</c>).
///
/// <para>MỘT con số dùng CHUNG cho hai đầu: (1) upload câu trả lời bị từ chối khi
/// <c>now &gt; deadline + grace</c> (AnswerService), (2) sweeper chỉ chốt buổi khi
/// <c>deadline + grace &lt; now</c> (SessionAbandonSweeper). Hai số khác nhau là tạo ra một khe:
/// sweeper chốt sớm hơn upload ⇒ câu trả lời cuối đang tải lên ăn "Buổi đã kết thúc" dù vẫn trong
/// ân hạn; upload chốt sớm hơn sweeper thì vô hại nhưng ân hạn hứa với ứng viên là nói dối.</para>
///
/// <para>Mặc định 30 giây trong CODE — không có biến môi trường bắt buộc mới
/// (<c>SessionDeadline__GraceSeconds</c> chỉ để chỉnh khi cần).</para>
/// </summary>
public sealed class SessionDeadlineOptions
{
    public const string SectionName = "SessionDeadline";

    public int GraceSeconds { get; set; } = 30;

    /// <summary>Âm ⇒ 0 (ân hạn âm nghĩa là chốt TRƯỚC hạn — vô nghĩa).</summary>
    public TimeSpan Grace => TimeSpan.FromSeconds(Math.Max(0, GraceSeconds));
}
