namespace PaymentService.Models
{
    /// <summary>
    /// Cấu hình <c>OrderExpiryReconciler</c> — đóng đơn Pending quá hạn sang Expired (PAY-10).
    /// <c>Enabled=false</c> = tắt an toàn (không quét, không đụng đơn nào).
    /// </summary>
    public class OrderExpirySettings
    {
        public const string SectionName = "OrderExpiry";
        public bool Enabled { get; set; } = true;
        public int ScanIntervalSeconds { get; set; } = 300;
        /// <summary>
        /// Ân hạn SAU <c>expired_at</c> mới xét đóng — chừa thời gian cho webhook Paid về muộn
        /// (webhook là nguồn chân lý PAY-8; đóng sớm hơn webhook sẽ khoá luôn đường cộng credit).
        /// </summary>
        public int GracePeriodMinutes { get; set; } = 10;
        public int BatchSize { get; set; } = 200;
    }
}
