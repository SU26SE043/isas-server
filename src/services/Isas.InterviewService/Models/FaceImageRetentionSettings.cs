namespace Isas.InterviewService.Models
{
    /// <summary>
    /// B2C coaching (2026-09-17) — cấu hình job dọn ảnh webcam quá hạn (<see cref="Entities.PracticeFaceImage"/>).
    /// Mirror <c>CampaignService.Models.FaceImageRetentionSettings</c> (BK25).
    ///
    /// Ảnh bình thường được xoá NGAY sau detect (xem <c>PracticeFaceCheckService</c>); job này chỉ
    /// dọn phần SÓT (AI lỗi/hết giờ, chết giữa chừng, xoá S3 hụt). Mặc định TẮT theo tiền lệ mọi
    /// job purge của repo (S8 P1 + BK25: bật lần đầu phải quan sát một chu kỳ) — nhưng vì đây là
    /// dữ liệu sinh trắc học không ai đọc lại, nên bật với <c>RetentionDays</c> NGẮN (1) là hợp lý.
    /// </summary>
    public class FaceImageRetentionSettings
    {
        public const string SectionName = "FaceImageRetention";

        /// <summary>false = không xoá gì (chỉ ghi sổ). Xem lý do mặc định TẮT ở trên.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Số ngày giữ ảnh tính từ <c>CapturedAt</c>.</summary>
        public int RetentionDays { get; set; } = 90;

        /// <summary>Trần số ảnh xử lý mỗi vòng — mỗi ảnh = 1 lời gọi DeleteObject sang SeaweedFS.</summary>
        public int BatchSize { get; set; } = 200;

        /// <summary>Nhịp quét (giây). Mặc định 1 giờ — retention tính bằng ngày, không cần gấp.</summary>
        public int ScanIntervalSeconds { get; set; } = 3600;
    }
}
