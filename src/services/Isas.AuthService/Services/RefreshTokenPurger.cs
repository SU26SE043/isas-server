using Isas.AuthService.Models;
using Microsoft.Extensions.Options;

namespace Isas.AuthService.Services
{
    /// <summary>
    /// DB28 — job nền dọn <c>refresh_tokens</c> đã chết (xem <see cref="RefreshTokenPurge"/> cho vị ngữ
    /// xoá + bất biến an toàn, và <see cref="RefreshTokenRetentionSettings"/> cho lý do bảng này phải dọn).
    ///
    /// Mirror idiom sweeper bên Payment: delay khởi động, interval config-được, try/catch mỗi vòng
    /// (1 lỗi không giết service), scope-per-scan cho DbContext, <c>Enabled=false</c> = tắt an toàn.
    /// Job này CHỈ xoá — vòng lỗi/bỏ qua không gây hại gì ngoài việc rác nằm lại tới vòng sau.
    /// </summary>
    public class RefreshTokenPurger : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly RefreshTokenRetentionSettings _options;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RefreshTokenPurger> _logger;

        public RefreshTokenPurger(
            IServiceScopeFactory scopeFactory,
            IOptions<RefreshTokenRetentionSettings> options,
            IConfiguration configuration,
            ILogger<RefreshTokenPurger> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("RefreshTokenPurger TẮT (RefreshTokenRetention:Enabled=false) — không dọn refresh_tokens");
                return;
            }

            // Chờ 1 nhịp cho app khởi động xong trước khi quét lần đầu.
            await Task.Delay(TimeSpan.FromSeconds(60), ct);

            var interval = TimeSpan.FromMinutes(_options.ScanIntervalMinutes > 0 ? _options.ScanIntervalMinutes : 60);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    // Bỏ qua vòng này = không xoá gì (an toàn tuyệt đối cho job xoá dữ liệu).
                    _logger.LogError(ex, "Lỗi khi dọn refresh_tokens (bỏ qua vòng này, KHÔNG xoá row nào)");
                }

                await Task.Delay(interval, ct);
            }
        }

        private async Task ScanOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            var graceSeconds = int.TryParse(_configuration["Jwt:RefreshTokenGraceSeconds"], out var g) && g >= 0 ? g : 60;
            var retentionDays = RefreshTokenPurge.ResolveRetentionDays(_options, graceSeconds);

            var deleted = await RefreshTokenPurge.PurgeAsync(db, DateTime.UtcNow, _options, graceSeconds, ct);

            if (deleted > 0)
                _logger.LogInformation(
                    "Đã dọn {Deleted} refresh_tokens chết (ngưỡng giữ {RetentionDays} ngày, ân hạn {GraceSeconds}s)",
                    deleted, retentionDays, graceSeconds);
        }
    }
}
