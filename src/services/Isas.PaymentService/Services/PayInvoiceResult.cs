using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Kết quả tất toán hóa đơn (P8b). Tách outcome khỏi HTTP để unit-test service không cần controller:
    /// <see cref="PayInvoiceOutcome.Created"/> → 200 + link PayOS · <see cref="PayInvoiceOutcome.NotFound"/> →
    /// 404 (không tồn tại / chủ khác) · <see cref="PayInvoiceOutcome.NotPayable"/> → 409 (đã Paid/Void).
    /// </summary>
    public sealed record PayInvoiceResult(PayInvoiceOutcome Outcome, OrderResponse? Order)
    {
        public static PayInvoiceResult Created(OrderResponse order) => new(PayInvoiceOutcome.Created, order);
        public static PayInvoiceResult NotFound() => new(PayInvoiceOutcome.NotFound, null);
        public static PayInvoiceResult NotPayable() => new(PayInvoiceOutcome.NotPayable, null);
    }

    public enum PayInvoiceOutcome
    {
        Created,
        NotFound,
        NotPayable
    }
}
