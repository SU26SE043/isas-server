namespace Isas.InterviewService.Entities;

/// <summary>
/// Một tín hiệu MẤT TẬP TRUNG trong buổi luyện B2C — 1 dòng = 1 lần rời tab / dán / mất focus.
///
/// Chỉ B2C (`practice_sessions.campaign_id IS NULL`) và chỉ khi buổi đó bật
/// <see cref="PracticeSession.FocusTrackingEnabled"/>. Buổi B2B có đường riêng ở CampaignService
/// (`session_flags`) phục vụ HR — hai bảng CỐ Ý tách rời vì hai đối tượng đọc khác nhau và vì
/// `session_flags.campaign_id` là NOT NULL + FK, không chứa nổi buổi không có campaign.
///
/// FK cứng tới `practice_sessions` (Cascade) — cùng DB, cùng service nên KHÔNG vi phạm GEN-2;
/// xoá buổi là xoá sạch dấu vết, không để lại dòng mồ côi.
/// </summary>
public class PracticeFocusEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    /// <summary>Một trong <see cref="Services.FocusSignals.Allowed"/>; CHECK ở DB chốt lớp cuối.</summary>
    public string SignalType { get; set; } = null!;

    /// <summary>Chi tiết ngắn cho người luyện đọc lại (vd "rời tab 12 giây"). Tuỳ chọn.</summary>
    public string? Note { get; set; }

    /// <summary>Thời điểm SERVER nhận. KHÔNG nhận mốc thời gian do client gửi — nó tự khai được.</summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public PracticeSession Session { get; set; } = null!;
}
