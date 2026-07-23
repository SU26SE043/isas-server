namespace Isas.PaymentService.Models
{
    /// <summary>
    /// F23/BK24 — cấu hình job đóng dấu hóa đơn Overdue. Bind section `InvoiceOverdue`.
    /// <see cref="Enabled"/> MẶC ĐỊNH <c>false</c>: bật job này = bật phanh BK17 (chặn reserve khi org
    /// có hóa đơn Overdue) cho TOÀN HỆ ngay lập tức — phải là quyết định vận hành riêng sau khi soi dữ
    /// liệu `invoices` thật, không phải mặc định đi kèm code (tiền lệ 3 job purge S8 P1).
    /// </summary>
    public class InvoiceOverdueSettings
    {
        public bool Enabled { get; set; } = false;
        public int ScanIntervalSeconds { get; set; } = 600;

        /// <summary>Số giờ ân hạn sau `due_at` trước khi đóng dấu Overdue.</summary>
        public int GraceHours { get; set; } = 24;
    }
}
