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
    }
}
