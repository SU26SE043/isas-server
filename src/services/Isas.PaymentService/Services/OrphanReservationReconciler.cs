using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// DB18 (DB4b) — reconciler bù trừ orphan reservation. Luồng Start reserve-first: reserve credit
    /// (key=session_id, UNIQUE) TRƯỚC khi insert practice_session vào Interview DB. Nếu process CRASH giữa
    /// reserve↔insert, <c>credit_reservations</c> có row <c>Reserved</c> với <c>session_id</c> KHÔNG BAO GIỜ
    /// có session → orphan (giữ credit vĩnh viễn). try/catch release lúc Start chỉ cover in-process exception;
    /// DB4 <c>CreditReservationReconciler</c> đếm orphan là hợp lệ (không release). Đây là hở còn lại.
    ///
    /// Compensation-reconciler NHẸ (KHÔNG full-saga, KHÔNG bảng saga): quét reservation Reserved quá
    /// ngưỡng tuổi → hỏi Interview session nào THỰC SỰ tồn tại → session không tồn tại = orphan → release
    /// (idempotent/absorbing PAY-11 — chỉ release nếu còn Reserved). Bao cả B2B (owner=Org) + B2C
    /// (owner=User) + lesson (mọi session là row practice_sessions) — không phân biệt owner, chỉ theo
    /// session-existence.
    ///
    /// AN TOÀN: CHỈ release khi Interview XÁC NHẬN DƯƠNG không-tồn-tại (call thành công + session ∉ existing).
    /// Interview down/lỗi → <c>GetExistingSessionsAsync</c> NÉM → exception nổi khỏi ScanOnceAsync → catch
    /// vòng ngoài → SKIP cả vòng, KHÔNG release ai (tránh release oan reservation hợp lệ khi không xác minh
    /// được). TUYỆT ĐỐI không coi "call lỗi" = "không tồn tại".
    ///
    /// Mirror <see cref="CreditReservationReconciler"/>: interval config-được, delay khởi động, try/catch
    /// mỗi vòng (1 lỗi KHÔNG giết service), scope-per-scan cho DbContext (BackgroundService = singleton).
    /// </summary>
    public class OrphanReservationReconciler : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly OrphanReconcileSettings _options;
        private readonly ILogger<OrphanReservationReconciler> _logger;

        public OrphanReservationReconciler(
            IServiceScopeFactory scopeFactory,
            IOptions<OrphanReconcileSettings> options,
            ILogger<OrphanReservationReconciler> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Chờ 1 nhịp cho app + Interview khởi động xong trước khi quét lần đầu.
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
                    // Không để 1 vòng lỗi (kể cả Interview down → InterviewServiceException) giết background
                    // service. Skip vòng = KHÔNG release ai (an toàn: không xác minh được thì không đụng).
                    _logger.LogError(ex, "Lỗi khi đối soát orphan reservation (bỏ qua vòng này, KHÔNG release)");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo: CreditReservationReconciler/SettlementReconciler).
        private async Task ScanOnceAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return;   // safe-disable

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

            var thresholdMinutes = _options.OrphanThresholdMinutes > 0 ? _options.OrphanThresholdMinutes : 10;
            var batchSize = _options.BatchSize > 0 ? _options.BatchSize : 200;
            var cutoff = DateTime.UtcNow.AddMinutes(-thresholdMinutes);

            // Ứng viên orphan: Reserved + quá ngưỡng tuổi (chưa quá ngưỡng = có thể insert đang dở → bỏ qua).
            var sessionIds = await db.CreditReservations
                .Where(r => r.Status == ReservationStatus.Reserved && r.CreatedAt < cutoff)
                .OrderBy(r => r.CreatedAt)
                .Take(batchSize)
                .Select(r => r.SessionId)
                .ToListAsync(ct);

            if (sessionIds.Count == 0) return;

            // XÁC MINH DƯƠNG với Interview. NÉM (Interview down) → nổi ra ngoài → skip vòng, KHÔNG release.
            var interview = scope.ServiceProvider.GetRequiredService<IInterviewSessionClient>();
            var existing = await interview.GetExistingSessionsAsync(sessionIds, ct);

            // Orphan = reservation Reserved quá ngưỡng mà session KHÔNG tồn tại (xác nhận dương).
            var orphans = sessionIds.Where(id => !existing.Contains(id)).ToList();
            if (orphans.Count == 0) return;

            var accountService = scope.ServiceProvider.GetRequiredService<ICreditAccountService>();
            var released = 0;
            foreach (var sessionId in orphans)
            {
                try
                {
                    // ReleaseAsync idempotent/absorbing (PAY-11): chỉ release nếu còn Reserved; đã
                    // Consumed/Released → no-op (KHÔNG hoàn oan). owner lấy từ reservation (nguồn chân lý).
                    var result = await accountService.ReleaseAsync(sessionId, ct);
                    if (result.Outcome == ReleaseOutcome.Released) released++;
                }
                catch (Exception ex)
                {
                    // Lỗi release 1 reservation → bỏ qua nó, KHÔNG giết cả vòng (các orphan khác vẫn xử lý).
                    _logger.LogError(ex,
                        "Không thể release orphan reservation session {SessionId}, bỏ qua", sessionId);
                }
            }

            if (released > 0)
                _logger.LogWarning(
                    "OrphanReservationReconciler: release {Count}/{Total} reservation mồ côi (session không tồn tại — crash reserve↔insert lúc Start)",
                    released, orphans.Count);
        }
    }
}
