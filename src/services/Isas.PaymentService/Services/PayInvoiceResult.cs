using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Kết quả tất toán hóa đơn (P8b). Tách outcome khỏi HTTP để unit-test service không cần controller:
    /// <see cref="PayInvoiceOutcome.Created"/> → 200 + link PayOS · <see cref="PayInvoiceOutcome.NotFound"/> →
    /// 404 (không tồn tại / chủ khác) · <see cref="PayInvoiceOutcome.NotPayable"/> → 409 (đã Paid/Void) ·
    /// <see cref="PayInvoiceOutcome.AlreadyPending"/> → 409 (đơn Pending còn sống — PP6).
    /// </summary>
    public sealed record PayInvoiceResult(PayInvoiceOutcome Outcome, OrderResponse? Order)
    {
        public static PayInvoiceResult Created(OrderResponse order) => new(PayInvoiceOutcome.Created, order);
        public static PayInvoiceResult NotFound() => new(PayInvoiceOutcome.NotFound, null);
        public static PayInvoiceResult NotPayable() => new(PayInvoiceOutcome.NotPayable, null);

        /// <summary>
        /// PP6 — đã có đơn PayOS đang chờ trả cho ĐÚNG hóa đơn này (Pending, chưa hết <c>ExpiredAt</c>).
        /// Trả kèm đơn ĐANG SỐNG đó (KHÔNG có <c>CheckoutUrl</c> — PayOS không cho lấy lại link của một
        /// orderCode đã tạo, và cũng không cho tạo link thứ hai cho CÙNG orderCode) để client biết đơn nào
        /// đang chờ và tự đối chiếu qua <c>GET /order/{id}/status</c> (P3), thay vì đoán mù.
        /// </summary>
        public static PayInvoiceResult AlreadyPending(OrderResponse order) => new(PayInvoiceOutcome.AlreadyPending, order);
    }

    public enum PayInvoiceOutcome
    {
        Created,
        NotFound,
        NotPayable,
        AlreadyPending
    }
}
