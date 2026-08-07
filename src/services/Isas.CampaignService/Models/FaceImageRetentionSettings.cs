namespace Isas.CampaignService.Models
{
    /// <summary>
    /// BK25 — cấu hình job dọn ảnh sinh trắc học quá hạn (<see cref="FaceImage"/>).
    ///
    /// 🔴 <b>Mặc định TẮT</b>, KHÁC <c>Outbox:PurgeEnabled</c> (mặc định bật). Lý do không phải sự
    /// thận trọng chung chung: outbox-row bị xoá là rác thuần (mail đã gửi, dedup nằm ở cột khác),
    /// còn ở đây thứ bị xoá là <b>bằng chứng chống gian lận của một buổi thi</b> — xoá nhầm thì
    /// không có đường dựng lại, và tiền lệ trong repo (3 job purge của S8 P1) là bật lần đầu phải
    /// quan sát một chu kỳ rồi mới mở. Bật bằng <c>FaceImageRetention__Enabled=true</c>.
    /// </summary>
    public class FaceImageRetentionSettings
    {
        public const string SectionName = "FaceImageRetention";

        /// <summary>false = không xoá gì (chỉ ghi sổ). Xem lý do mặc định TẮT ở trên.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Số ngày giữ ảnh tính từ <see cref="FaceImage.CapturedAt"/>. Mặc định 90 theo
        /// <b>CAMP-13/D11</b> ("purge file S3 sau 90 ngày, giữ điểm/transcript") — điểm và cờ
        /// <c>session_flags</c> KHÔNG nằm trong phạm vi job này nên vẫn còn nguyên cho HR đối chất.
        /// </summary>
        public int RetentionDays { get; set; } = 90;

        /// <summary>
        /// Trần số ảnh xử lý mỗi vòng. Mỗi ảnh = 1 lời gọi DeleteObject sang SeaweedFS, nên trần
        /// này giữ vòng quét ngắn và không nện S3 khi dọn tồn đọng lớn.
        /// </summary>
        public int BatchSize { get; set; } = 200;

        /// <summary>Nhịp quét (giây). Mặc định 1 giờ — retention tính bằng ngày, không cần gấp.</summary>
        public int ScanIntervalSeconds { get; set; } = 3600;
    }
}
