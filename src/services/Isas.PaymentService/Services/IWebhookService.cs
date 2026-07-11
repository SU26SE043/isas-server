namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P2 — xử lý webhook PayOS đã verify. Tách khỏi phần verify chữ ký (ở WebhookController) để
    /// unit-test được logic cộng credit trên SQLite mà không cần PayOS thật.
    /// </summary>
    public interface IWebhookService
    {
        /// <summary>
        /// Áp 1 webhook <c>Paid</c> đã verify (idempotent theo <paramref name="payosOrderCode"/> — PAY-8):
        /// <list type="number">
        ///   <item>order Pending→Paid ATOMIC (WHERE status=Pending) — 0 row = terminal ⇒ no-op (PAY-10,
        ///   KHÔNG cộng credit lần 2).</item>
        ///   <item>đảm bảo ví tồn tại → <c>remaining_credits += package.interview_credits</c> ATOMIC.</item>
        ///   <item>ghi <c>credit_transactions(Purchase, +credits)</c> + <c>payment_transactions</c> (append-only).</item>
        /// </list>
        /// Không khớp đơn nào → chỉ ghi <c>payment_transactions</c> log (order_id null) → no-op. Tất cả trong 1 transaction.
        /// </summary>
        Task<WebhookApplyOutcome> ApplyPaidWebhookAsync(long payosOrderCode, string? gatewayTxnId, string rawPayload, CancellationToken ct = default);
    }

    public enum WebhookApplyOutcome
    {
        /// <summary>Đơn Pending→Paid + đã cộng credit (lần đầu).</summary>
        Credited,
        /// <summary>Đơn InvoiceSettlement Pending→Paid + hóa đơn Issued/Overdue→Paid (KHÔNG cộng credit) — P8b.</summary>
        InvoiceSettled,
        /// <summary>Đơn đã terminal (Paid/Expired/…) — idempotent no-op, KHÔNG cộng lần 2.</summary>
        AlreadyProcessed,
        /// <summary>Không có đơn khớp payos_order_code — chỉ log bằng chứng, no-op.</summary>
        OrderNotFound
    }
}
