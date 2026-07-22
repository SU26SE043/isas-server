using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;

namespace Isas.PaymentService.Models
{
    /// <summary>
    /// F23/BK24 — đóng dấu Issued → Overdue khi quá `due_at + GraceHours`. Đây LÀ thứ kích hoạt guard
    /// BK17 (<c>CreditAccountService.ReserveAsync</c>: <c>hasOverdue</c> → 402) — job này TẮT thì guard
    /// BK17 mãi mãi là dead code dù bản thân code đúng.
    ///
    /// Mirror idiom <see cref="SubscriptionExpiryReconciler"/>/<see cref="CreditReservationReconciler"/>:
    /// interval config-được, delay khởi động 30s, try/catch mỗi vòng (1 lỗi không giết service),
    /// scope-per-scan cho DbContext (qua IInvoiceService).
    /// </summary>
    public class InvoiceOverdueReconciler : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly InvoiceOverdueSettings _options;
        private readonly ILogger<InvoiceOverdueReconciler> _logger;

        public InvoiceOverdueReconciler(
            IServiceScopeFactory scopeFactory,
            IOptions<InvoiceOverdueSettings> options,
            ILogger<InvoiceOverdueReconciler> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            var interval = TimeSpan.FromSeconds(_options.ScanIntervalSeconds > 0 ? _options.ScanIntervalSeconds : 600);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi đóng dấu hóa đơn Overdue");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo).
        private async Task ScanOnceAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return;   // safe-disable — mặc định TẮT (xem InvoiceOverdueSettings)

            using var scope = _scopeFactory.CreateScope();
            var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceService>();

            var marked = await invoices.MarkOverdueInvoicesAsync(_options.GraceHours, ct);
            if (marked > 0)
                _logger.LogWarning("Đã đóng dấu Overdue cho {Count} hóa đơn quá hạn tất toán", marked);
        }
    }
}
