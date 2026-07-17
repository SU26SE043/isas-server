namespace Isas.InterviewService.Models;

// DB2 — cấu hình OutboxDispatcher (BackgroundService quét outbox_messages phát settlement-event).
public class OutboxSettings
{
    public const string SectionName = "Outbox";

    // false = tắt dispatcher (an toàn: không tự publish). Mặc định bật (event thật phải được phát).
    public bool Enabled { get; set; } = true;

    // Nhịp quét (giây). Event thật nên quét nhanh hơn reconciler (120s) — mặc định 15s.
    public int ScanIntervalSeconds { get; set; } = 15;

    // Số row publish tối đa mỗi vòng (chặn ôm quá nhiều 1 lần khi tồn đọng lớn).
    public int BatchSize { get; set; } = 100;
}
