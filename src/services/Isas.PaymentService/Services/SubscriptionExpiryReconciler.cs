using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// F8 — đóng dấu <c>Active → Expired</c> cho kỳ hạn thuê bao đã quá <c>expires_at</c>.
    ///
    /// THUẦN DỌN DẸP/BÁO CÁO. Luật vào bài KHÔNG phụ thuộc job này:
    /// <see cref="ISubscriptionService.HasActiveAsync"/> tự so <c>expires_at &gt; now</c>, nên sweeper
    /// chết/chậm cũng không biến một thuê bao hết hạn thành quyền thi miễn phí. Đây là lựa chọn có chủ ý —
    /// bài học ngược lại của <see cref="OrderExpiryReconciler"/> (ở đó Expired là nhánh chết vì KHÔNG có
    /// sweeper, và trạng thái thật sự phụ thuộc vào việc có ai đóng dấu hay không).
    ///
    /// An toàn theo chiều ngược lại: job chỉ đẩy Active→Expired khi đã quá hạn, không bao giờ mở lại
    /// quyền, nên chạy trùng/chạy nhiều lần đều idempotent.
    ///
    /// Mirror idiom <see cref="CreditReservationReconciler"/>: interval config-được, delay khởi động,
    /// try/catch mỗi vòng (1 lỗi không giết service), scope-per-scan cho DbContext.
    /// </summary>
    public class SubscriptionExpiryReconciler : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ReconcileSettings _options;
        private readonly ILogger<SubscriptionExpiryReconciler> _logger;

        public SubscriptionExpiryReconciler(
            IServiceScopeFactory scopeFactory,
            IOptions<ReconcileSettings> options,
            ILogger<SubscriptionExpiryReconciler> logger)
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
                    _logger.LogError(ex, "Lỗi khi đóng dấu thuê bao hết hạn");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo).
        private async Task ScanOnceAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return;   // safe-disable

            using var scope = _scopeFactory.CreateScope();
            var subscriptions = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

            var closed = await subscriptions.ExpireDueAsync(ct);
            if (closed > 0)
                _logger.LogInformation("Đã đóng dấu Expired cho {Count} kỳ hạn thuê bao quá hạn", closed);
        }
    }
}
