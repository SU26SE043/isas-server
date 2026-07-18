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

    // ── DB28: dọn outbox đã phát ──────────────────────────────────────────
    // Row đã publish KHÔNG bao giờ được xoá → bảng chỉ có phình. Partial index
    // `WHERE published_at IS NULL` giữ đường dispatch nhanh, nên hỏng ÂM THẦM: không phải latency
    // mà là đĩa/vacuum/backup. Purge = XOÁ DỮ LIỆU nên có công tắc riêng, chỉ đụng row ĐÃ publish
    // và quá hạn, xoá theo batch có trần, log số row xoá.

    // false = tắt hẳn purge (không xoá gì). Bật mặc định: row đã phát + quá hạn không còn giá trị
    // vận hành (đối soát đã xong), giữ lại chỉ tốn chỗ.
    public bool PurgeEnabled { get; set; } = true;

    // Giữ row đã publish bao nhiêu ngày trước khi xoá. 30 ngày = đủ rộng cho mọi lần đối soát
    // sự cố broker/consumer thực tế (dài hơn hẳn cửa sổ điều tra vài ngày), vẫn chặn phình vô hạn.
    // Đặt ≤ 0 = coi như TẮT purge (không diễn giải thành "xoá tất").
    public int PurgeRetentionDays { get; set; } = 30;

    // Trần số row xoá mỗi lần chạy (chia nhỏ nhiều batch) — DELETE lớn khoá lâu + phình WAL.
    public int PurgeBatchSize { get; set; } = 1000;

    // Số batch tối đa mỗi vòng quét ⇒ trần tuyệt đối = PurgeBatchSize × PurgeMaxBatchesPerScan.
    public int PurgeMaxBatchesPerScan { get; set; } = 10;

    // Nhịp purge (phút) — dọn rác không cần gấp như dispatch.
    public int PurgeIntervalMinutes { get; set; } = 60;
}
