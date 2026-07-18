using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Đóng đơn <c>Pending</c> quá hạn sang <see cref="OrderStatus.Expired"/> (PAY-10).
    ///
    /// Bug bắt ở e2e 2026-07-18: <c>OrderStatus.Expired</c> KHÔNG được gán ở bất kỳ đâu trong service —
    /// không có sweeper nào đóng đơn. DB thật lúc đó: 16 đơn Pending, cả 16 đã quá <c>expired_at</c>,
    /// 0 đơn từng Expired. Hệ quả: đơn bỏ dở tồn vĩnh viễn, UI hiện "Đang chờ thanh toán" mãi, và PAY-10
    /// (4 trạng thái terminal) chỉ đúng trên giấy vì Expired là nhánh chết.
    ///
    /// ĐỐI SOÁT TRƯỚC, ĐÓNG SAU — KHÔNG đóng mù. Đóng mù là mất tiền thật: user trả ở phút chót, webhook
    /// về trễ, sweeper đã đóng Expired ⇒ terminal bất biến (PAY-10) chặn luôn đường cộng credit ⇒ user
    /// trả tiền mà không có credit, phải đối soát tay. Nên mỗi đơn ứng viên đều HỎI PayOS trước
    /// (<see cref="IPayOsQueryClient"/>) và:
    /// <list type="bullet">
    ///   <item><b>Paid</b> → REUSE <see cref="IWebhookService.ApplyPaidWebhookAsync"/> (một đường cộng
    ///   credit duy nhất, idempotent theo <c>payos_order_code</c> — PAY-8) rồi BỎ QUA, không đóng.
    ///   Sweeper vì thế còn là lưới an toàn cho webhook rơi.</item>
    ///   <item><b>Underpaid</b> → BỎ QUA + cảnh báo. Tiền đã chuyển một phần: tự đẩy sang terminal là
    ///   chôn ca cần đối soát tay. Để người quyết.</item>
    ///   <item><b>PayOS lỗi/không với tới</b> → BỎ QUA đơn đó, giữ Pending, vòng sau thử lại. TUYỆT ĐỐI
    ///   không coi "hỏi không được" = "chưa trả" (cùng nguyên tắc an toàn với
    ///   <see cref="OrphanReservationReconciler"/>).</item>
    ///   <item>Còn lại (Pending/Processing/Cancelled/Expired/Failed phía PayOS) → link chết thật →
    ///   đóng Expired.</item>
    /// </list>
    ///
    /// Flip có guard <c>WHERE status = Pending</c> (ExecuteUpdate atomic): webhook/poll chạy song song vừa
    /// set Paid thì 0 row → sweeper KHÔNG ghi đè Paid thành Expired.
    ///
    /// Mirror idiom <see cref="OrphanReservationReconciler"/>: interval config-được, delay khởi động,
    /// try/catch mỗi vòng (1 lỗi không giết service), scope-per-scan cho DbContext.
    /// </summary>
    public class OrderExpiryReconciler : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly OrderExpirySettings _options;
        private readonly ILogger<OrderExpiryReconciler> _logger;

        public OrderExpiryReconciler(
            IServiceScopeFactory scopeFactory,
            IOptions<OrderExpirySettings> options,
            ILogger<OrderExpiryReconciler> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Chờ 1 nhịp cho app khởi động xong trước khi quét lần đầu.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            var interval = TimeSpan.FromSeconds(_options.ScanIntervalSeconds > 0 ? _options.ScanIntervalSeconds : 300);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    // 1 vòng lỗi không được giết background service; skip vòng = không đóng ai (an toàn).
                    _logger.LogError(ex, "Lỗi khi đối soát đơn quá hạn (bỏ qua vòng này, KHÔNG đóng đơn nào)");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo: CreditReservationReconciler/OrphanReservationReconciler).
        private async Task ScanOnceAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return;   // safe-disable

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

            var graceMinutes = _options.GracePeriodMinutes > 0 ? _options.GracePeriodMinutes : 10;
            var batchSize = _options.BatchSize > 0 ? _options.BatchSize : 200;
            var cutoff = DateTime.UtcNow.AddMinutes(-graceMinutes);

            // Ứng viên: Pending + đã qua expired_at + hết ân hạn (chưa hết ân hạn = webhook còn có thể về).
            var candidates = await db.Orders
                .AsNoTracking()
                .Where(o => o.Status == OrderStatus.Pending && o.ExpiredAt < cutoff)
                .OrderBy(o => o.ExpiredAt)
                .Take(batchSize)
                .Select(o => new { o.Id, o.PayosOrderCode })
                .ToListAsync(ct);

            if (candidates.Count == 0) return;

            var payos = scope.ServiceProvider.GetRequiredService<IPayOsQueryClient>();
            var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookService>();

            var expired = 0;
            var rescued = 0;

            foreach (var order in candidates)
            {
                PayOsPaymentInfo info;
                try
                {
                    info = await payos.GetPaymentInfoAsync(order.PayosOrderCode, ct);
                }
                catch (Exception ex)
                {
                    // Không xác minh được → GIỮ Pending, vòng sau thử lại. Không đóng mù.
                    _logger.LogWarning(ex,
                        "PayOS get-payment-info lỗi cho orderCode={OrderCode} — giữ Pending, thử lại vòng sau.",
                        order.PayosOrderCode);
                    continue;
                }

                // Đã trả thật nhưng webhook rơi → cứu bằng đường cộng credit chuẩn (idempotent, PAY-8).
                if (info.Status == PayOsPaymentStatus.Paid)
                {
                    try
                    {
                        await webhooks.ApplyPaidWebhookAsync(
                            order.PayosOrderCode, info.GatewayTxnId, info.RawPayload ?? "{}", ct);
                        rescued++;
                        _logger.LogWarning(
                            "Đơn orderCode={OrderCode} quá hạn nhưng PayOS báo Paid — đã cộng credit (webhook rơi), KHÔNG đóng.",
                            order.PayosOrderCode);
                    }
                    catch (Exception ex)
                    {
                        // Cộng credit lỗi → KHÔNG đóng đơn (giữ Pending để còn cứu được vòng sau).
                        _logger.LogError(ex,
                            "Đơn orderCode={OrderCode} PayOS báo Paid nhưng cộng credit lỗi — giữ Pending.",
                            order.PayosOrderCode);
                    }
                    continue;
                }

                // Tiền đã chuyển một phần → không tự đẩy sang terminal, để đối soát tay.
                if (info.Status == PayOsPaymentStatus.Underpaid)
                {
                    _logger.LogWarning(
                        "Đơn orderCode={OrderCode} quá hạn ở trạng thái Underpaid — CẦN ĐỐI SOÁT TAY, giữ Pending.",
                        order.PayosOrderCode);
                    continue;
                }

                // Link chết thật → đóng. Guard WHERE status=Pending: webhook vừa set Paid → 0 row → không ghi đè.
                var moved = await db.Orders
                    .Where(o => o.Id == order.Id && o.Status == OrderStatus.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.Status, OrderStatus.Expired)
                        // DB14 — ExecuteUpdate bỏ qua SaveChanges override → stamp updated_at tường minh.
                        .SetProperty(o => o.UpdatedAt, _ => DateTime.UtcNow), ct);

                expired += moved;
            }

            if (expired > 0 || rescued > 0)
                _logger.LogInformation(
                    "OrderExpiryReconciler: đóng {Expired} đơn quá hạn, cứu {Rescued} đơn đã trả (webhook rơi) trên {Total} ứng viên.",
                    expired, rescued, candidates.Count);
        }
    }
}
