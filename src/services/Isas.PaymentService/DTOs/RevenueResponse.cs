using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    /// <summary>Một dòng doanh thu theo loại đơn.</summary>
    public class RevenueByKindRow
    {
        public OrderKind Kind { get; set; }
        public long AmountVnd { get; set; }
        public int OrderCount { get; set; }
    }

    /// <summary>Một mốc trong chuỗi thời gian (đầu ngày / đầu tháng, UTC).</summary>
    public class RevenueBucketRow
    {
        public DateTime PeriodStart { get; set; }
        public long AmountVnd { get; set; }
        public int OrderCount { get; set; }
    }

    /// <summary>
    /// F19 (gross margin/funnel) — phễu chuyển đổi đơn hàng trong kỳ, đếm theo <c>created_at</c> (KHÔNG
    /// phải <c>paid_at</c> — đơn Pending/Failed/Expired/Cancelled không có <c>paid_at</c>, nên đếm theo đó
    /// sẽ xoá sạch mọi đơn không sống sót tới Paid, đúng thứ phễu cần đo).
    ///
    /// Đây là bức tranh KHÁC hẳn <see cref="RevenueReportResponse.GrossRevenueVnd"/> ở trên: gross đếm
    /// đơn Paid/Refunded theo THỜI ĐIỂM THU (có thể là đơn được TẠO ở kỳ trước nhưng thu tiền kỳ này), còn
    /// phễu đếm đơn theo THỜI ĐIỂM TẠO (dù nó có bao giờ được trả hay không). Hai trục thời gian khác
    /// nhau — đừng cộng chéo hai bộ số của hai bảng này.
    /// </summary>
    public class RevenueFunnelRow
    {
        public int CreatedCount { get; set; }
        /// <summary>Đơn tạo trong kỳ mà nay ở trạng thái Paid HOẶC Refunded — "đã từng thu tiền", bất kể
        /// sau đó có bị hoàn hay không (hoàn không xoá dấu vết đã từng chuyển đổi thành công).</summary>
        public int PaidCount { get; set; }
        public int FailedCount { get; set; }
        public int ExpiredCount { get; set; }
        public int CancelledCount { get; set; }
        public int PendingCount { get; set; }

        /// <summary><see cref="PaidCount"/> / <see cref="CreatedCount"/> * 100. 0 khi
        /// <see cref="CreatedCount"/> = 0 (không chia cho 0).</summary>
        public double ConversionRatePct { get; set; }
    }

    /// <summary>
    /// F19 — báo cáo doanh thu một kỳ.
    ///
    /// <para><b>Vì sao doanh thu GỘP và tiền hoàn được tách làm hai con số thay vì một.</b> Đơn được hoàn
    /// rời khỏi trạng thái <c>Paid</c> (F18), nên nếu chỉ cộng đơn Paid thì một khoản hoàn phát sinh
    /// tháng này sẽ âm thầm rút tiền khỏi doanh thu THÁNG TRƯỚC — báo cáo đã chốt tự đổi số. Vì vậy:
    /// doanh thu gộp đếm theo <c>paid_at</c>, tiền hoàn đếm theo <c>refunded_at</c>, và
    /// <see cref="NetRevenueVnd"/> là hiệu của hai con số TRONG CÙNG KỲ.</para>
    ///
    /// <para><b>Credit tặng không bao giờ lọt vào đây.</b> Báo cáo đọc bảng <c>orders</c>; quà (F7
    /// <c>FreeGrant</c>, F20 <c>PromoGrant</c>) chỉ ghi <c>credit_transactions</c> và không sinh đơn nào
    /// ⇒ không có đường nào cộng quà thành doanh thu. Có test khoá điều này.</para>
    /// </summary>
    public class RevenueReportResponse
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string Granularity { get; set; } = null!;

        /// <summary>Tổng tiền các đơn Paid có <c>paid_at</c> trong kỳ.</summary>
        public long GrossRevenueVnd { get; set; }
        public int PaidOrderCount { get; set; }

        /// <summary>Tổng tiền các đơn được hoàn có <c>refunded_at</c> trong kỳ (số dương).</summary>
        public long RefundedVnd { get; set; }
        public int RefundedOrderCount { get; set; }

        /// <summary><see cref="GrossRevenueVnd"/> − <see cref="RefundedVnd"/>.</summary>
        public long NetRevenueVnd { get; set; }

        public List<RevenueByKindRow> ByKind { get; set; } = [];
        public List<RevenueBucketRow> Buckets { get; set; } = [];

        // ── giá vốn AI + biên lợi nhuận gộp ─────────────────────────────────────────────────────

        /// <summary>Chi phí AI (Gemini + whisper-1) phát sinh TRONG kỳ, đọc từ <c>ai_usage_logs</c> qua
        /// <c>IAiUsageService</c> — CÙNG kỳ <c>[from,to)</c> với doanh thu ở trên (khớp theo
        /// <c>created_at</c> của dòng usage, không phải theo đơn hàng nào).</summary>
        public decimal AiCostUsd { get; set; }
        public long AiCostVnd { get; set; }

        /// <summary><see cref="NetRevenueVnd"/> − <see cref="AiCostVnd"/>. CÓ THỂ ÂM nếu chi phí AI vượt
        /// doanh thu kỳ đó — KHÔNG kẹp về 0, số âm là tín hiệu tài chính THẬT (kỳ này đang lỗ vận hành
        /// AI); che nó đi là nói dối báo cáo.</summary>
        public long GrossMarginVnd { get; set; }

        // ── tỷ lệ hoàn + ARPU ────────────────────────────────────────────────────────────────────

        /// <summary><see cref="RefundedVnd"/> / <see cref="GrossRevenueVnd"/> * 100. CÓ THỂ VƯỢT 100%:
        /// refund đếm theo <c>refunded_at</c>, gross đếm theo <c>paid_at</c> (xem class-doc phía trên) —
        /// hai kỳ khác kỳ đo là chuyện thật (đơn hoàn tháng này của một đơn thu tháng trước). Không kẹp,
        /// số bất thường là điều nên thấy chứ không phải điều nên giấu.</summary>
        public double RefundRatePct { get; set; }

        /// <summary>Số chủ ví (<c>owner_id</c> distinct) có ít nhất một đơn Paid/Refunded (theo
        /// <c>paid_at</c>) trong kỳ.</summary>
        public int PayingOwnerCount { get; set; }

        /// <summary><see cref="GrossRevenueVnd"/> / <see cref="PayingOwnerCount"/>. 0 khi
        /// <see cref="PayingOwnerCount"/> = 0 (không chia cho 0).</summary>
        public long ArpuVnd { get; set; }

        // ── phễu chuyển đổi đơn hàng ─────────────────────────────────────────────────────────────

        public RevenueFunnelRow Funnel { get; set; } = new();
    }
}
