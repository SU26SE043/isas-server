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

            // PayOS ≠ Paid: lưu bằng chứng payload (append-only) để đối soát — nhưng CHỈ khi trạng thái PayOS
            // ĐỔI so với dòng bằng chứng cuối của đơn. FE poll 2s/lần tới 45 lần; trước bản này mỗi lượt poll
            // là một dòng `pending` y hệt nhau — đo prod: 404/427 dòng của bảng bằng-chứng-đối-soát là rác
            // poll, một đơn 122 dòng. Bằng chứng có giá trị là CHUYỂN TIẾP (pending→underpaid…), không phải
            // "vẫn đang chờ" lặp lại.
            var evidenceStatus = info.Status.ToString().ToLowerInvariant();
            if (!string.IsNullOrEmpty(info.RawPayload))
            {
                var lastStatus = await _db.PaymentTransactions
                    .AsNoTracking()
                    .Where(t => t.OrderId == order.Id)
                    .OrderByDescending(t => t.CreatedAt)
                    .Select(t => t.Status)
                    .FirstOrDefaultAsync(ct);

                if (!string.Equals(lastStatus, evidenceStatus, StringComparison.Ordinal))
                {
                    _db.PaymentTransactions.Add(new PaymentTransaction
                    {
                        Id = Guid.NewGuid(),
                        OrderId = order.Id,
                        Gateway = "payos",
                        GatewayTxnId = info.GatewayTxnId,
                        Status = evidenceStatus,
                        RawWebhookPayload = info.RawPayload,
                        CreatedAt = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync(ct);
                }
            }

            // PayOS `Cancelled` = link đã bị huỷ (user bấm Huỷ trên trang PayOS / merchant huỷ) — link chết,
            // PayOS KHÔNG còn nhận tiền cho nó ⇒ đóng đơn `Cancelled` NGAY (PAY-10: user chủ động huỷ), thay vì
            // để Pending tới lúc OrderExpiryReconciler quét (~45'). Trước bản này FE nhận Pending mãi nên poll
            // hết 90s rồi mới báo "thất bại", mỗi lượt poll = 1 call PayOS.
            // CHỈ Cancelled: Expired/Failed/Processing/Pending phía PayOS vẫn để sweeper xử sau ân hạn — ở đó
            // còn phải hỏi lại PayOS trước khi đóng (đóng mù là mất tiền thật, xem OrderExpiryReconciler).
            // Guard WHERE status=Pending: webhook Paid vừa lật thì 0 row → không ghi đè Paid (bất biến terminal).
            if (info.Status == PayOsPaymentStatus.Cancelled)
            {
                var closed = await _db.Orders
                    .Where(o => o.Id == order.Id && o.Status == OrderStatus.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.Status, OrderStatus.Cancelled)
                        // DB14 — ExecuteUpdate bỏ qua SaveChanges override → stamp updated_at tường minh.
                        .SetProperty(o => o.UpdatedAt, _ => DateTime.UtcNow), ct);

                if (closed == 1)
                {
                    _logger.LogInformation(
                        "Đơn orderCode={OrderCode}: PayOS báo link Cancelled → đóng Cancelled tại poll.",
                        order.PayosOrderCode);
                    return new OrderStatusResult(order.PayosOrderCode, OrderStatus.Cancelled, null);
                }

                // 0 row = luồng khác đã chốt (webhook Paid / sweeper) → trả trạng thái hiện tại.
                var current = await _db.Orders.AsNoTracking()
                    .Where(o => o.Id == order.Id)
                    .Select(o => new { o.Status, o.PaidAt })
                    .FirstAsync(ct);
                return new OrderStatusResult(order.PayosOrderCode, current.Status, current.PaidAt);
            }

            return new OrderStatusResult(order.PayosOrderCode, order.Status, order.PaidAt);
        }
    }
}
