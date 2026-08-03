using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Theo tiếp những lệnh chi hoàn tiền đang bay cho tới khi có kết luận.
    ///
    /// <para><b>Vì sao cần một job nền chứ không chỉ xử lý ngay trong request:</b> chuyển khoản liên ngân
    /// hàng không xong trong một nhịp HTTP. Lệnh vừa tạo gần như luôn trả về "đang xử lý", nên nếu chỉ
    /// dựa vào lời gọi ban đầu thì mọi lệnh sẽ nằm mãi ở trạng thái đang-bay và không đơn nào được đóng
    /// dấu đã hoàn — đúng cái bệnh "quên chuyển tiền" mà tính năng này sinh ra để chữa, chỉ đổi chỗ.</para>
    ///
    /// <para><b>Vì sao nó cũng là lưới cứu ca timeout:</b> khi lời gọi tạo lệnh không trả về được, ta
    /// không biết tiền đã đi hay chưa. Khoá idempotency đã được ghi xuống đĩa TRƯỚC lời gọi, nên vòng
    /// quét sau gọi lại bằng đúng khoá đó — payOS nhận ra lệnh trùng và không chuyển lần hai.</para>
    ///
    /// Mẫu <see cref="OrphanReservationReconciler"/>: interval config-được, delay khởi động, try/catch
    /// mỗi vòng (một lỗi KHÔNG giết service), scope-per-scan cho DbContext.
    /// </summary>
    public class RefundPayoutReconciler : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly RefundPayoutSettings _options;
        private readonly ILogger<RefundPayoutReconciler> _logger;

        public RefundPayoutReconciler(
            IServiceScopeFactory scopeFactory,
            IOptions<RefundPayoutSettings> options,
            ILogger<RefundPayoutReconciler> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            var interval = TimeSpan.FromSeconds(_options.ScanIntervalSeconds > 0 ? _options.ScanIntervalSeconds : 120);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    // Một vòng lỗi không được giết background service. Bỏ vòng = KHÔNG kết luận gì về
                    // lệnh nào — an toàn, vì mọi lệnh vẫn giữ nguyên trạng thái đang bay.
                    _logger.LogError(ex, "Lỗi khi đối soát lệnh chi hoàn tiền (bỏ qua vòng này).");
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

            var batchSize = _options.BatchSize > 0 ? _options.BatchSize : 100;
            // Chờ một nhịp trước khi hỏi: hỏi ngay sau khi gửi thì gần như chắc chắn nhận về "đang xử lý",
            // chỉ tốn request mà không biết thêm gì.
            var cutoff = DateTime.UtcNow.AddSeconds(-(_options.PollAfterSeconds > 0 ? _options.PollAfterSeconds : 60));

            var orderIds = await db.Orders
                .Where(o => o.PayoutStatus == PayoutStatus.InFlight
                            && o.RefundSettledAt == null
                            && o.UpdatedAt < cutoff)
                .OrderBy(o => o.UpdatedAt)
                .Take(batchSize)
                .Select(o => o.Id)
                .ToListAsync(ct);

            if (orderIds.Count == 0) return;

            var refunds = scope.ServiceProvider.GetRequiredService<IRefundService>();
            var settled = 0;
            var failed = 0;

            foreach (var orderId in orderIds)
            {
                try
                {
                    var result = await refunds.PollRefundPayoutAsync(orderId, ct);

                    if (result.Outcome == RefundPayoutOutcome.Settled) settled++;
                    else if (result.Outcome is RefundPayoutOutcome.Rejected or RefundPayoutOutcome.NameMismatch)
                    {
                        failed++;
                        _logger.LogError(
                            "Lệnh chi hoàn tiền đơn {OrderId} kết thúc bất thường: {Outcome} — {Message}",
                            orderId, result.Outcome, result.Message);
                    }
                }
                catch (Exception ex)
                {
                    // Lỗi một đơn không được làm hỏng cả vòng — các đơn còn lại vẫn phải được theo tiếp.
                    _logger.LogError(ex, "Không đối soát được lệnh chi của đơn {OrderId}, bỏ qua.", orderId);
                }
            }

            if (settled > 0)
                _logger.LogInformation("Đã đóng dấu hoàn tiền cho {Count} đơn (payOS xác nhận chuyển xong).", settled);
            if (failed > 0)
                _logger.LogWarning("{Count} lệnh chi hoàn tiền cần người xử lý.", failed);
        }
    }
}
