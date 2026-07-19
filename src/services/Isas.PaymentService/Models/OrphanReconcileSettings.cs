namespace PaymentService.Models
{
    /// <summary>
    /// DB18 (DB4b) — cấu hình <c>OrphanReservationReconciler</c>: release reservation <c>Reserved</c> mà
    /// session Interview KHÔNG BAO GIỜ được tạo (crash giữa reserve↔insert lúc Start → orphan giữ credit
    /// vĩnh viễn). Bind section <c>OrphanReconcile</c>. Config thuần (không cột DB) → KHÔNG migration.
    /// <see cref="Enabled"/>=false → tắt hẳn (safe-disable). <see cref="OrphanThresholdMinutes"/> = tuổi
    /// tối thiểu của reservation Reserved mới xét orphan (insert xảy ra mili-giây sau reserve → quá ngưỡng
    /// mà chưa có session = orphan thật; tránh đua với insert đang dở).
    /// </summary>
    public class OrphanReconcileSettings
    {
        public const string SectionName = "OrphanReconcile";
        public bool Enabled { get; set; } = true;
        public int ScanIntervalSeconds { get; set; } = 120;
        public int OrphanThresholdMinutes { get; set; } = 10;
        public int BatchSize { get; set; } = 200;

        /// <summary>
        /// R1 — công tắc riêng cho nhánh CONSUME (session <c>Scored</c> mà chỗ giữ còn <c>Reserved</c> →
        /// trừ 1 credit). Mặc định BẬT: deploy phát là vá thật. Tách khỏi <see cref="Enabled"/> vì đây là
        /// nhánh DUY NHẤT tự trừ tiền người dùng — tắt được ngay bằng env, khỏi phải rollback image, mà
        /// vẫn giữ nhánh release (an toàn) chạy tiếp.
        /// </summary>
        public bool ConsumeTerminalScored { get; set; } = true;

        /// <summary>
        /// R1 — MỐC TUYỆT ĐỐI: chỉ consume chỗ giữ có <c>created_at</c> ≥ mốc này. KHÔNG cấu hình → lấy
        /// mốc KHỞI ĐỘNG của reconciler ⇒ nghĩa mặc định là "chỉ từ nay về sau", ops không phải làm gì.
        ///
        /// Vì sao mốc tuyệt đối chứ KHÔNG phải cửa sổ trượt ("bỏ qua reservation quá N ngày"): tồn đọng đo
        /// được chỉ ~1 ngày tuổi nên cửa sổ trượt vẫn nuốt chúng — đúng thứ cần tránh. Chỗ giữ tồn đọng là
        /// hệ quả sự cố hạ tầng của CHÚNG TA (mất event settle), không phải lỗi người dùng ⇒ để NGƯỜI đối
        /// soát tay (OPS2), không để máy tự trừ hồi tố.
        ///
        /// Muốn quét cả tồn đọng thì đặt mốc sớm hơn TƯỜNG MINH.
        /// ⚠ Hệ quả: restart dịch vụ đẩy mốc mặc định tiến lên ⇒ chỗ giữ sinh ngay trước restart có thể
        /// không bao giờ được consume. Hở nhỏ, nhưng reconciler PHẢI log rõ mỗi ca bị bỏ (không im lặng).
        /// </summary>
        public DateTime? ConsumeFromUtc { get; set; }
    }
}
