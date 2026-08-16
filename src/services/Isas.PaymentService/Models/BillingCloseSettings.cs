namespace Isas.PaymentService.Models
{
    /// <summary>
    /// Cấu hình cho job chốt kỳ hoá đơn postpaid tự động.
    /// </summary>
    public class BillingCloseSettings
    {
        /// <summary>
        /// Xác định xem job có được bật hay không. Mặc định là false, vì bật job này sẽ khiến hệ thống tự lập hoá đơn thực tế cho mọi org trả sau, phải là quyết định vận hành riêng sau khi soi dữ liệu invoices, không phải mặc định đi kèm code.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Interval thời gian (đơn vị giây) giữa các lần thực hiện job chốt kỳ hoá đơn.
        /// </summary>
        public int ScanIntervalSeconds { get; set; } = 3600;
    }
}
