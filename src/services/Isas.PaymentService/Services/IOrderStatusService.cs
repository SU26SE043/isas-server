using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P3 — active-polling đối soát (payment.md §Thanh toán webhook + active polling). FE gọi
    /// <c>GET /order/{id}/status</c>; nếu server CHƯA nhận webhook (order còn Pending) → chủ động hỏi PayOS
    /// get-payment-info NGAY để cứu ca webhook delay/drop. Tách khỏi controller/PayOS SDK để unit-test SQLite.
    /// </summary>
    public interface IOrderStatusService
    {
        /// <summary>
        /// Đối soát trạng thái đơn cho chủ ví <paramref name="ownerType"/>/<paramref name="ownerId"/>:
        /// <list type="bullet">
        ///   <item>đơn không tồn tại HOẶC của chủ ví khác → <c>null</c> (controller → 404, KHÔNG lộ đơn người khác).</item>
        ///   <item>đơn TERMINAL (Paid/Expired/Failed/Cancelled) → trả trạng thái hiện tại, KHÔNG gọi PayOS (PAY-10 bất biến).</item>
        ///   <item>đơn Pending → hỏi PayOS: Paid → reuse <see cref="IWebhookService.ApplyPaidWebhookAsync"/>
        ///   (idempotent, cộng credit + log); ≠ Paid → lưu bằng chứng (append-only) + giữ Pending.</item>
        /// </list>
        /// </summary>
        Task<OrderStatusResult?> GetOrderStatusAsync(Guid orderId, OwnerType ownerType, Guid ownerId, CancellationToken ct = default);
    }

    /// <summary>Kết quả đối soát trả về controller (map DTO <c>OrderStatusResponse</c>).</summary>
    public sealed record OrderStatusResult(long OrderCode, OrderStatus Status, DateTime? PaidAt);
}
