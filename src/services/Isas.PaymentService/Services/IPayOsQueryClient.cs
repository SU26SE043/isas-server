namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P3 — abstraction đối soát PayOS (active-polling). Tách khỏi SDK <c>PayOSClient</c> để unit-test
    /// logic đối soát trên SQLite mà KHÔNG cần PayOS thật — giống cách P2 tách verify (WebhookController)
    /// khỏi apply (WebhookService). Impl thật <see cref="PayOsQueryClient"/> bọc
    /// <c>payOS.PaymentRequests.GetAsync(orderCode)</c> (getPaymentLinkInformation).
    /// </summary>
    public interface IPayOsQueryClient
    {
        /// <summary>
        /// Hỏi PayOS trạng thái hiện tại của link theo <paramref name="orderCode"/>. Trả trạng thái đã map
        /// + reference giao dịch (nếu có) + payload gốc (JSON) để lưu bằng chứng đối soát.
        /// </summary>
        Task<PayOsPaymentInfo> GetPaymentInfoAsync(long orderCode, CancellationToken ct = default);
    }

    /// <summary>Kết quả đối soát PayOS (đã map khỏi SDK để service không phụ thuộc kiểu SDK).</summary>
    public sealed record PayOsPaymentInfo(PayOsPaymentStatus Status, string? GatewayTxnId, string? RawPayload);

    /// <summary>
    /// Trạng thái PayOS đã chuẩn hoá. Chỉ <see cref="Paid"/> mới kích hoạt cộng credit (đối soát ngay);
    /// còn lại coi như chưa trả xong (FE tiếp tục poll). Map từ <c>PaymentLinkStatus</c> của SDK.
    /// </summary>
    public enum PayOsPaymentStatus
    {
        Pending,
        Processing,
        Underpaid,
        Paid,
        Cancelled,
        Expired,
        Failed
    }
}
