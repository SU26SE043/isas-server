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
    }
}
