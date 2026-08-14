using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;

namespace Isas.PaymentService.Models
{
    /// <summary>
    /// PP2 — tự động CHỐT KỲ hoá đơn postpaid: mỗi vòng quét gọi `IInvoiceService.CloseDuePeriodsAsync`
    /// để lập hoá đơn cho tháng dương lịch UTC vừa kết thúc, cho mọi ví Org đang Postpaid.
    ///
    /// Hậu quả khi job này TẮT: không ai chốt kỳ ⇒ `period_usage` cứ tăng tới `credit_limit` ⇒
    /// org trả sau bị 402 vĩnh viễn mà KHÔNG có hoá đơn nào để trả (hỏng câm, không cảnh báo).
    ///
    /// Mặc định TẮT vì job này LẬP HOÁ ĐƠN THẬT.
    ///
    /// Mirror idiom của các reconciler khác: interval config-được, delay khởi động 30s,
    /// try/catch mỗi vòng, scope-per-scan cho DbContext (qua IInvoiceService).
    /// </summary>
    public class BillingCloseReconciler : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly BillingCloseSettings _options;
        private readonly ILogger<BillingCloseReconciler> _logger;

        public BillingCloseReconciler(
            IServiceScopeFactory scopeFactory,
            IOptions<BillingCloseSettings> options,
            ILogger<BillingCloseReconciler> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            var interval = TimeSpan.FromSeconds(_options.ScanIntervalSeconds > 0 ? _options.ScanIntervalSeconds : 3600);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi chốt kỳ hóa đơn postpaid");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo).
        private async Task ScanOnceAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return;   // safe-disable — mặc định TẮT (xem BillingCloseSettings)

            using var scope = _scopeFactory.CreateScope();
            var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceService>();

            var closed = await invoices.CloseDuePeriodsAsync(DateTime.UtcNow, ct);
            if (closed > 0)
                _logger.LogInformation("Đã chốt {Count} kỳ hóa đơn postpaid", closed);
        }
    }
}
