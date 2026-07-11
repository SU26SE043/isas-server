using static Isas.PaymentService.DTOs.InvoiceRequest;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P8b — hóa đơn postpaid (payment.md §Postpaid chốt kỳ + §Invoice). CHỈ Org: chốt kỳ (snapshot
    /// period_usage → hóa đơn Issued) → tất toán qua PayOS (Order kind=InvoiceSettlement) → webhook Paid →
    /// Issued→Paid (WebhookService branch theo Kind). KHÔNG cộng credit ở đường tất toán.
    /// </summary>
    public interface IInvoiceService
    {
        /// <summary>
        /// Chốt kỳ 1 org trong <b>1 transaction</b> (payment.md §Postpaid): snapshot <c>period_usage</c> →
        /// tạo <see cref="Invoice"/>(<c>Issued</c>, <c>interview_count = period_usage</c>,
        /// <c>amount = interview_count × unit_price</c> — unit_price lấy config <c>Billing:UnitPrice</c>) →
        /// reset <c>period_usage = 0</c>. Fail giữa chừng → rollback cả 2 (không mất/nhân nợ). Ví không tồn
        /// tại → <see cref="KeyNotFoundException"/>.
        /// </summary>
        Task<InvoiceResponse> CloseBillingPeriodAsync(Guid orgId, DateTime? periodStart = null, DateTime? periodEnd = null, CancellationToken ct = default);

        /// <summary>
        /// Tất toán hóa đơn (owner-scope): tạo đơn <c>InvoiceSettlement</c> + link PayOS (REUSE OrderService).
        /// Hóa đơn không tồn tại / của chủ khác → <see cref="PayInvoiceOutcome.NotFound"/>; đã Paid/Void →
        /// <see cref="PayInvoiceOutcome.NotPayable"/> (no-op); còn Issued/Overdue → <see cref="PayInvoiceOutcome.Created"/>.
        /// </summary>
        Task<PayInvoiceResult> PayInvoiceAsync(OwnerType ownerType, Guid ownerId, Guid invoiceId, CancellationToken ct = default);

        /// <summary>Hóa đơn của chủ ví (owner-scope) — mới nhất trước.</summary>
        Task<List<InvoiceResponse>> GetInvoicesAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default);

        /// <summary>1 hóa đơn (owner-scope): không tồn tại / của chủ khác → null (controller → 404).</summary>
        Task<InvoiceResponse?> GetInvoiceAsync(OwnerType ownerType, Guid ownerId, Guid invoiceId, CancellationToken ct = default);
    }
}
