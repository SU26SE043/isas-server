namespace Isas.PaymentService.DTOs
{
    /// <summary>
    /// Chỉ số tài chính kiểu SỐ DƯ TẠI MỘT THỜI ĐIỂM (snapshot) — KHÁC BẢN CHẤT với báo cáo doanh thu
    /// (<see cref="RevenueReportResponse"/>, dòng chảy trong kỳ nửa mở <c>[from,to)</c>). Công nợ phải
    /// thu (AR) và doanh thu định kỳ hiện hành (MRR) không có ý nghĩa "trong kỳ này" — chúng là
    /// "TÍNH TỚI BÂY GIỜ" (<see cref="AsOf"/>), đúng như cách kế toán đọc bảng cân đối kế toán
    /// (balance sheet) khác báo cáo kết quả kinh doanh (income statement). Vì vậy endpoint này KHÔNG
    /// nhận tham số <c>from</c>/<c>to</c>.
    /// </summary>
    public class FinanceSnapshotResponse
    {
        public DateTime AsOf { get; set; }
        public OutstandingReceivablesRow OutstandingReceivables { get; set; } = new();

        /// <summary>Monthly Recurring Revenue — quy đổi mọi thuê bao Annual về đơn giá THÁNG.</summary>
        public decimal MrrVnd { get; set; }

        /// <summary>Số CHỦ VÍ đang có thuê bao hiệu lực (không phải số row — một chủ ví có thể có nhiều
        /// row <c>Active</c> chồng lấn, xem <see cref="OutstandingReceivablesRow"/> và ghi chú service).</summary>
        public int ActiveSubscriptionCount { get; set; }
    }

    /// <summary>
    /// Công nợ phải thu — hoá đơn postpaid (chỉ Org, <c>Invoice.OwnerType=Org</c>) CHƯA thanh toán, tách
    /// <c>Issued</c> (còn hạn) khỏi <c>Overdue</c> (quá hạn, rủi ro nợ xấu cao hơn — cần đối soát ưu
    /// tiên). <c>Void</c> (hoá đơn đã huỷ) và <c>Paid</c> KHÔNG tính — cả hai không còn là công nợ.
    /// </summary>
    public class OutstandingReceivablesRow
    {
        public decimal IssuedVnd { get; set; }
        public int IssuedCount { get; set; }
        public decimal OverdueVnd { get; set; }
        public int OverdueCount { get; set; }

        /// <summary><see cref="IssuedVnd"/> + <see cref="OverdueVnd"/>.</summary>
        public decimal TotalVnd { get; set; }
    }
}
