namespace Isas.InterviewService.Models
{
    /// <summary>
    /// B2C coaching (2026-09-17) — cấu hình job dọn ảnh webcam quá hạn (<see cref="Entities.PracticeFaceImage"/>).
    /// Mirror <c>CampaignService.Models.FaceImageRetentionSettings</c> (BK25).
    ///
    /// 🔴 Mặc định TẮT: xoá nhầm là mất bằng chứng không dựng lại được. Tiền lệ trong repo (3 job
    /// purge của S8 P1 + BK25) là bật lần đầu phải quan sát một chu kỳ rồi mới mở tường minh bằng
    /// <c>FaceImageRetention__Enabled=true</c>.
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
