using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Services;

/// <summary>
/// ATT1 — NGUỒN DUY NHẤT cho luật "đồng hồ cả buổi" của bài thi B2B.
///
/// <para>Đồng hồ chạy từ lúc VÀO PHÒNG (begin), không phải lúc bấm Bắt đầu ở Campaign: giữa hai mốc
/// đó là bước chuẩn bị (kiểm thiết bị, xác minh khuôn mặt). Vì vậy đề chỉ được lộ SAU begin — ba
/// cửa phải cùng đóng: GET session che nội dung, speech và upload trả 409. Hở một cửa là ứng viên
/// đọc đề trong lúc chuẩn bị và luật "đồng hồ chạy từ lúc vào phòng" vô nghĩa.</para>
///
/// <para>Buổi KHÔNG tính giờ (mọi buổi B2C · buổi B2B tạo trước ATT1 · chiến dịch không khai thời
/// lượng) đi qua đây với <see cref="IsTimed"/> = false ⇒ không che, không chặn — hành vi y hệt
/// trước ATT1.</para>
/// </summary>
public static class SessionTiming
{
    public const int MinDurationMinutes = 5;
    public const int MaxDurationMinutes = 180;

    /// <summary>Buổi có đồng hồ cả buổi: chỉ B2B (campaign) và chỉ khi có thời lượng.</summary>
    public static bool IsTimed(PracticeSession s) => s.CampaignId != null && s.DurationMinutes != null;

    /// <summary>Buổi tính giờ mà CHƯA vào phòng ⇒ đề bị che, upload/speech bị chặn.</summary>
    public static bool IsLocked(PracticeSession s) => IsTimed(s) && s.BegunAt == null;

    /// <summary>
    /// Trạng thái đã chốt — begin trả 409 SESSION_ENDED. Ready/InProgress/GeneratingQuestions không
    /// thuộc tập này. (Tập này rộng hơn guard upload — upload không chặn <c>Failed</c> vì lý do lịch sử;
    /// begin thì chặn: vào phòng của một buổi đã hỏng không có nghĩa.)
    /// </summary>
    public static bool IsEnded(SessionStatus status) => status is SessionStatus.Scoring
        or SessionStatus.Scored or SessionStatus.Completed
        or SessionStatus.SessionAbandoned or SessionStatus.Failed;

    /// <summary>
    /// Hạn chót mới lúc begin = min(hạn cứng đang có, now + thời lượng). Hạn cứng (hết hạn chiến
    /// dịch / khung giờ) LUÔN thắng nếu gần hơn — begin chỉ được rút ngắn, không được kéo dài.
    /// </summary>
    public static DateTime ComputeBegunDeadline(DateTime? hardDeadline, DateTime now, int durationMinutes)
    {
        var byDuration = now.AddMinutes(durationMinutes);
        return hardDeadline is DateTime hard && hard < byDuration ? hard : byDuration;
    }

    /// <summary>
    /// Đã QUÁ hạn + ân hạn chưa (so nghiêm ngặt: đúng mốc deadline + grace vẫn còn nhận). CÙNG nghĩa
    /// với điều kiện sweeper <c>deadline &lt; now − grace</c> — xem <see cref="SweepCutoff"/>.
    /// </summary>
    public static bool IsPastGrace(DateTime? deadline, DateTime now, TimeSpan grace)
        => deadline is DateTime d && now > d + grace;

    /// <summary>
    /// Mốc cắt cho sweeper: buổi có <c>deadline &lt; cutoff</c> ⇔ <c>deadline + grace &lt; now</c>.
    /// Viết dưới dạng cutoff (không phải <c>deadline + grace</c>) để vị từ SQL vẫn là
    /// <c>deadline &lt; @p</c> ⇒ partial index <c>ix_practice_sessions_deadline</c> còn dùng được.
    /// </summary>
    public static DateTime SweepCutoff(DateTime now, TimeSpan grace) => now - grace;
}
