using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P3 — đối soát trạng thái đơn qua active-polling (payment.md §Thanh toán · §State machine Order).
    /// Cộng credit khi PayOS báo Paid bằng cách REUSE <see cref="IWebhookService.ApplyPaidWebhookAsync"/>
    /// (một đường cộng credit duy nhất — idempotent theo <c>payos_order_code</c>, PAY-8). Terminal bất biến
    /// (PAY-10): đơn đã Paid/Expired/Failed/Cancelled → KHÔNG gọi PayOS, KHÔNG cộng lại.
    /// </summary>
    public class OrderStatusService : IOrderStatusService
    {
        private readonly PaymentDbContext _db;
        private readonly IPayOsQueryClient _payos;
        private readonly IWebhookService _webhooks;
        private readonly ILogger<OrderStatusService> _logger;

        public OrderStatusService(
            PaymentDbContext db,
            IPayOsQueryClient payos,
            IWebhookService webhooks,
            ILogger<OrderStatusService> logger)
        {
            _db = db;
            _payos = payos;
            _webhooks = webhooks;
            _logger = logger;
        }

        public async Task<OrderStatusResult?> GetOrderStatusAsync(
            Guid orderId, OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            var order = await _db.Orders
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orderId, ct);

            // Không tồn tại → 404. Owner-scope: đơn của chủ ví khác cũng → 404 (không lộ đơn người khác).
            if (order is null || order.OwnerType != ownerType || order.OwnerId != ownerId)
                return null;

            // Order TERMINAL (Paid/Expired/Failed/Cancelled) — bất biến (PAY-10): trả trạng thái hiện tại,
            // KHÔNG gọi PayOS. Ca "đã Expired mà PayOS trả muộn Paid" cũng dừng ở đây → KHÔNG tự cộng credit.
            if (order.Status != OrderStatus.Pending)
                return new OrderStatusResult(order.PayosOrderCode, order.Status, order.PaidAt);

            // Order Pending: server chưa nhận webhook → chủ động đối soát PayOS NGAY.
            PayOsPaymentInfo info;
            try
            {
                info = await _payos.GetPaymentInfoAsync(order.PayosOrderCode, ct);
            }
            catch (Exception ex)
            {
                // PayOS lỗi/không với tới được → giữ nguyên trạng thái, trả Pending (FE cứ tiếp tục poll).
                _logger.LogWarning(ex,
                    "PayOS get-payment-info lỗi cho orderCode={OrderCode} — giữ Pending.", order.PayosOrderCode);
                return new OrderStatusResult(order.PayosOrderCode, order.Status, order.PaidAt);
            }

            if (info.Status == PayOsPaymentStatus.Paid)
            {
                // REUSE đường cộng credit của webhook (idempotent theo payos_order_code, PAY-8): order còn
                // Pending → guard WHERE status=Pending khớp → cộng credit + ghi ledger + payment_transactions.
                // Nếu đơn vừa bị webhook/poll khác cộng xong (Paid) → 0 row → AlreadyProcessed, KHÔNG cộng đôi.
                await _webhooks.ApplyPaidWebhookAsync(
                    order.PayosOrderCode, info.GatewayTxnId, info.RawPayload ?? "{}", ct);

                // Đọc lại trạng thái sau khi apply (ExecuteUpdate ghi thẳng DB) để trả về Paid + PaidAt.
                var applied = await _db.Orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == orderId, ct);

                return applied is null
                    ? new OrderStatusResult(order.PayosOrderCode, OrderStatus.Paid, DateTime.UtcNow)
                    : new OrderStatusResult(applied.PayosOrderCode, applied.Status, applied.PaidAt);
            }

            // PayOS ≠ Paid: chưa trả xong → giữ Pending. Lưu bằng chứng payload (append-only) nếu có để
            // đối soát về sau — soi gương status PayOS, KHÔNG tự quyết orders.status (nguồn chân lý là order).
            if (!string.IsNullOrEmpty(info.RawPayload))
            {
                _db.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    Gateway = "payos",
                    GatewayTxnId = info.GatewayTxnId,
                    Status = info.Status.ToString().ToLowerInvariant(),
                    RawWebhookPayload = info.RawPayload,
                    CreatedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(ct);
            }

            return new OrderStatusResult(order.PayosOrderCode, order.Status, order.PaidAt);
        }
    }
}
