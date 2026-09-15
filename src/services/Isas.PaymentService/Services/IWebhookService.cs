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

        /// <summary>
        /// Như overload trên nhưng có <paramref name="amountPaidVnd"/> = <c>data.amount</c> của webhook —
        /// số tiền của GIAO DỊCH vừa vào, KHÔNG phải số tiền của link (PayOS cho trả nhiều lần:
        /// <c>amountPaid</c>/<c>amountRemaining</c>, trạng thái <c>UNDERPAID</c>). Đường webhook PHẢI
        /// đi overload này: <c>success=true</c> chỉ nói "có tiền vào", không nói "đủ tiền".
        /// <list type="bullet">
        ///   <item><c>amountPaidVnd ≥ orders.amount_vnd</c> → áp như thường.</item>
        ///   <item>thiếu → hỏi lại PayOS trạng thái link (nhiều lần chuyển gộp đủ ⇒ PayOS báo <c>Paid</c>
        ///   ⇒ áp); PayOS chưa Paid / không hỏi được → ghi bằng chứng <c>underpaid</c>, đơn GIỮ Pending,
        ///   trả <see cref="WebhookApplyOutcome.Underpaid"/>. Fail-closed: đường poll/sweeper vẫn cứu
        ///   được về sau khi link thật sự đủ tiền.</item>
        /// </list>
        /// Overload không có số tiền (poll/sweeper) đi thẳng — ở đó PayOS đã trả <c>Paid</c> cho cả link.
        /// </summary>
        Task<WebhookApplyOutcome> ApplyPaidWebhookAsync(long payosOrderCode, long? amountPaidVnd, string? gatewayTxnId, string rawPayload, CancellationToken ct = default);
    }

    public enum WebhookApplyOutcome
    {
        /// <summary>Đơn Pending→Paid + đã cộng credit (lần đầu).</summary>
        Credited,
        /// <summary>Đơn InvoiceSettlement Pending→Paid + hóa đơn Issued/Overdue→Paid (KHÔNG cộng credit) — P8b.</summary>
        InvoiceSettled,
        /// <summary>
        /// F8 — đơn SubscriptionPurchase/Renewal Pending→Paid + kỳ hạn thuê bao đã kích hoạt.
        /// KHÔNG cộng credit, KHÔNG ghi <c>credit_transactions</c> (thuê bao mở khoá ở đường reserve,
        /// không phải bằng cách bơm credit vào ví).
        /// </summary>
        SubscriptionActivated,
        /// <summary>Đơn đã terminal (Paid/Expired/…) — idempotent no-op, KHÔNG cộng lần 2.</summary>
        AlreadyProcessed,
        /// <summary>Không có đơn khớp payos_order_code — chỉ log bằng chứng, no-op.</summary>
        OrderNotFound,
        /// <summary>
        /// Webhook mang <c>data.amount</c> NHỎ HƠN <c>orders.amount_vnd</c> và PayOS chưa xác nhận link
        /// đủ tiền — đơn GIỮ Pending, KHÔNG cộng credit, chỉ ghi bằng chứng <c>underpaid</c> để đối soát.
        /// </summary>
        Underpaid
    }
}
