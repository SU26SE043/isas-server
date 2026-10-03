namespace Isas.InterviewService.Services;

/// <summary>
/// Nguồn sự thật DUY NHẤT cho tín hiệu mất tập trung của buổi luyện B2C (coaching).
///
/// ⚠ KHÔNG phải anti-cheat: người luyện tự bật, trình duyệt/webcam của CHÍNH họ báo về, và chỉ họ
/// đọc. Ba vế đó khiến nó vô hiệu trước bất kỳ ai muốn gian lận — đừng đặt lại tên theo hướng hứa
/// nhiều hơn khả năng (repo đã trả giá cho `multi_voice`, `TimeLimitSeconds`, `ExampleAnswers`).
///
/// 2026-09-17 — mở rộng có chủ đích: thêm ĐẾM MẶT detect-only (<see cref="NoFace"/>/
/// <see cref="MultipleFaces"/>), KHÔNG ảnh mốc, KHÔNG so khớp danh tính (khác B2B `face_mismatch`).
/// Hai giá trị này KHÔNG nằm trong <see cref="Allowed"/>: client KHÔNG được tự khai chúng qua
/// `POST focus-events` (whitelist đó chỉ gồm 3 tín hiệu HÀNH VI do trình duyệt tự quan sát) — chúng
/// chỉ được PracticeFaceCheckService ghi sau khi hỏi AIService `/face-detect`. Tách hai tập
/// (<see cref="Allowed"/> vs <see cref="ServerOnly"/>) là lớp chặn đầu tiên chống giả mạo cờ.
///
/// 2026-10-03 — thêm <see cref="CameraBlocked"/> vào <see cref="Allowed"/> (client tự khai): khung
/// webcam tối/bị che chỉ TRÌNH DUYỆT thấy được (FE cố ý KHÔNG gửi ảnh đen cho AI — nhận về `no_face`
/// là gộp "che cam" với "rời chỗ", hai lời khuyên khác nhau). Trước đó FE chỉ hiện toast tại chỗ rồi
/// không ghi gì ⇒ màn kết quả không bao giờ biết người luyện đã che cam. Client tự khai được vì đây là
/// coaching: tự khai "tôi che cam" chỉ làm xấu kết quả của chính người khai, không ai khác đọc.
///
/// Giá trị phải khớp ĐÚNG CHECK `ck_practice_focus_events_signal_type` — có test khoá hai đầu.
/// </summary>
public static class FocusSignals
{
    public const string TabSwitch = "tab_switch";
    public const string Paste = "paste";
    public const string FocusLost = "focus_lost";
    public const string NoFace = "no_face";
    public const string MultipleFaces = "multiple_faces";
    public const string CameraBlocked = "camera_blocked";

    /// <summary>
    /// Tín hiệu client tự khai qua <c>POST focus-events</c>: 3 tín hiệu HÀNH VI + <see cref="CameraBlocked"/>
    /// (khung webcam tối/bị che — trình duyệt đo độ sáng tại chỗ, không có ảnh nào rời máy).
    /// </summary>
    public static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.Ordinal) { TabSwitch, Paste, FocusLost, CameraBlocked };

    /// <summary>
    /// Tín hiệu MẶT — chỉ server ghi (sau khi gọi AIService `/face-detect`), KHÔNG BAO GIỜ nhận từ
    /// client. Không cho <see cref="Isas.InterviewService.Services.PracticeService.RecordFocusEventAsync"/>
    /// nhận hai giá trị này — nếu không thì bất kỳ ai cũng tự khai "0 mặt/nhiều mặt" mà không cần
    /// gửi ảnh thật, phá đúng mục đích detect-only.
    /// </summary>
    public static readonly IReadOnlySet<string> ServerOnly =
        new HashSet<string>(StringComparer.Ordinal) { NoFace, MultipleFaces };

    /// <summary>Mọi giá trị hợp lệ ghi vào <c>practice_focus_events</c> — đúng CHECK ở DB.</summary>
    public static readonly IReadOnlySet<string> Persistable =
        new HashSet<string>(Allowed.Concat(ServerOnly), StringComparer.Ordinal);

    /// <summary>
    /// Trần số dòng MỘT BUỔI, DÙNG CHUNG cho cả hành vi lẫn mặt. Hành vi thật của một buổi 20–60
    /// phút hiếm khi quá vài chục sự kiện; trần này để một client lỗi (vòng lặp gửi liên tục) không
    /// bơm được hàng chục nghìn dòng vào bảng. Chạm trần → no-op idempotent (vẫn 204/200), KHÔNG
    /// lỗi: đây là số liệu coaching, mất một sự kiện sau dòng thứ 500 không đổi kết luận nào cho
    /// người đọc.
    /// </summary>
    public const int MaxEventsPerSession = 500;

    public static bool IsAllowed(string? signalType)
        => signalType is not null && Allowed.Contains(signalType);

    public static bool IsServerOnly(string? signalType)
        => signalType is not null && ServerOnly.Contains(signalType);
}
