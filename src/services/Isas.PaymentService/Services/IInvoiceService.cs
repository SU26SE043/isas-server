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
        public enum CloseBillingPeriodOutcome
        {
            Closed,

            /// <summary>Không có ví cho org này (thay cho KeyNotFoundException cũ).</summary>
            WalletMissing,

            /// <summary>Org đang Prepaid — không có kỳ postpaid nào để chốt.</summary>
            NotPostpaid,

            /// <summary>Billing:UnitPrice ≤ 0 (chưa cấu hình) — chặn lập hóa đơn 0đ (BK24 finding #4).</summary>
            UnitPriceNotConfigured,

            /// <summary>Kỳ này period_usage = 0, KHÔNG lập hoá đơn 0 đồng.</summary>
            NothingToBill,

            /// <summary>Đã có hoá đơn cho đúng kỳ đó rồi (chốt kỳ phải idempotent).</summary>
            AlreadyClosed
        }

        public sealed record CloseBillingPeriodResult(CloseBillingPeriodOutcome Outcome, InvoiceResponse? Invoice);

        /// <summary>
        /// Chốt kỳ 1 org trong <b>1 transaction</b> (payment.md §Postpaid): snapshot <c>period_usage</c> →
        /// tạo <see cref="Invoice"/>(<c>Issued</c>, <c>interview_count = period_usage</c>,
        /// <c>amount = interview_count × unit_price</c> — unit_price lấy config <c>Billing:UnitPrice</c>) →
        /// reset <c>period_usage = 0</c>. Fail giữa chừng → rollback cả 2 (không mất/nhân nợ). Ví không tồn
        /// tại → <see cref="KeyNotFoundException"/>.
        /// </summary>
        Task<CloseBillingPeriodResult> CloseBillingPeriodAsync(Guid orgId, DateTime? periodStart = null, DateTime? periodEnd = null, CancellationToken ct = default);

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

        /// <summary>
        /// F23/BK24 — quét Issued quá `due_at + graceHours` → Overdue (kích hoạt guard BK17 ở ReserveAsync).
        /// LOG riêng cho Issued mà DueAt=NULL (hóa đơn không bao giờ bị quét được — "phanh hỏng câm" phải
        /// nhìn thấy, không âm thầm bỏ qua mãi mãi). Trả số hóa đơn vừa đóng dấu.
        /// </summary>
        Task<int> MarkOverdueInvoicesAsync(int graceHours, CancellationToken ct = default);

        /// <summary>Chốt tự động THÁNG DƯƠNG LỊCH UTC vừa kết thúc cho MỌI ví Org đang Postpaid; trả về số hoá đơn THỰC SỰ lập được (NothingToBill/AlreadyClosed không tính).</summary>
        Task<int> CloseDuePeriodsAsync(DateTime asOfUtc, CancellationToken ct = default);
    }
}
