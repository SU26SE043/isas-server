namespace Isas.InterviewService.Services;

/// <summary>
/// Nguồn sự thật DUY NHẤT cho tín hiệu mất tập trung của buổi luyện B2C (coaching).
///
/// ⚠ KHÔNG phải anti-cheat: người luyện tự bật, trình duyệt của CHÍNH họ báo về, và chỉ họ đọc.
/// Ba vế đó khiến nó vô hiệu trước bất kỳ ai muốn gian lận — đừng đặt lại tên theo hướng hứa
/// nhiều hơn khả năng (repo đã trả giá cho `multi_voice`, `TimeLimitSeconds`, `ExampleAnswers`).
///
/// Whitelist CỐ Ý hẹp hơn B2B: `camera_blocked` và `monitoring_gap` chỉ có nghĩa khi có giám sát
/// webcam (OS chặn camera / nhịp chụp ảnh đứt), mà B2C đã chốt "chỉ cờ hành vi".
///
/// Giá trị phải khớp ĐÚNG CHECK `ck_practice_focus_events_signal_type` — có test khoá hai đầu.
/// </summary>
public static class FocusSignals
{
    public const string TabSwitch = "tab_switch";
    public const string Paste = "paste";
    public const string FocusLost = "focus_lost";

    public static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.Ordinal) { TabSwitch, Paste, FocusLost };

    /// <summary>
    /// Trần số dòng MỘT BUỔI. Hành vi thật của một buổi 20–60 phút hiếm khi quá vài chục sự kiện;
    /// trần này để một client lỗi (vòng lặp gửi liên tục) không bơm được hàng chục nghìn dòng vào
    /// bảng. Chạm trần → no-op idempotent (vẫn 204), KHÔNG lỗi: đây là số liệu coaching, mất một
    /// sự kiện sau dòng thứ 500 không đổi kết luận nào cho người đọc.
    /// </summary>
    public const int MaxEventsPerSession = 500;

    public static bool IsAllowed(string? signalType)
        => signalType is not null && Allowed.Contains(signalType);
}
