namespace Isas.CampaignService.Models
{
    // DB2b — cấu hình OutboxDispatcher (BackgroundService quét outbox_messages phát invitation-email).
    public class OutboxSettings
    {
        public const string SectionName = "Outbox";

        // false = tắt dispatcher (an toàn: không tự publish). Mặc định bật (email thật phải được phát).
        public bool Enabled { get; set; } = true;

        // Nhịp quét (giây). Mặc định 15s (email không cần realtime nhưng cũng không nên trễ quá).
        public int ScanIntervalSeconds { get; set; } = 15;

        // Số row publish tối đa mỗi vòng (chặn ôm quá nhiều 1 lần khi tồn đọng lớn).
        public int BatchSize { get; set; } = 100;

        // ── DB28: retention ────────────────────────────────────────────────────────
        // Row đã publish KHÔNG BAO GIỜ được xoá → bảng phình vô hạn (1 row/lời mời từng gửi).
        // Partial index `WHERE published_at IS NULL` giữ dispatcher nhanh nên hỏng ÂM THẦM:
        // không thấy ở latency, chỉ thấy ở đĩa/vacuum/backup.
        //
        // false = tắt hẳn purge (không xoá gì). Mặc định BẬT: row bị xoá là rác thuần —
        // mail đã gửi, và dedup chống gửi trùng nằm ở `campaign_invitations.email_sent_at`
        // chứ KHÔNG phải ở outbox-row. Xoá còn là điểm cộng bảo mật: payload chứa token
        // mời THÔ (DB23 chỉ hash trong bảng invitation), không nên nằm lại vô thời hạn.
        public bool PurgeEnabled { get; set; } = true;

        // Tuổi tối thiểu (ngày) tính từ published_at thì row mới được xoá. Rộng hơn nhiều
        // so với hạn dùng của lời mời → không đụng gì đang sống.
        public int PurgeRetentionDays { get; set; } = 30;

        // Trần số row xoá mỗi vòng — giữ transaction ngắn, không khoá bảng khi dọn tồn đọng lớn.
        public int PurgeBatchSize { get; set; } = 500;

        // Nhịp quét purge (giây). Mặc định 1 giờ — retention tính bằng ngày, không cần gấp.
        public int PurgeIntervalSeconds { get; set; } = 3600;

        // Row thử publish quá ngần này lần mà vẫn chưa đi được = bất thường, KHÔNG phải tồn
        // đọng khoẻ mạnh → log Warning để có cái mà cảnh báo. CHỈ cảnh báo: không xoá,
        // không dead-letter row chưa publish (mail chưa gửi = không được phép mất).
        // 0 = tắt cảnh báo.
        public int AlertAfterAttempts { get; set; } = 10;
    }
}
