using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// DB4 — reconciler BẤT BIẾN: <c>credit_accounts.reserved_credits ==
    /// count(credit_reservations status=Reserved)</c> cho CÙNG (owner_type, owner_id). Crash giữa
    /// reserve/consume/release hoặc bút toán lệch có thể làm <c>reserved_credits</c> drift khỏi count
    /// thật → chặn oan reserve mới hoặc rò credit giữ chỗ. Quét định kỳ TỪ PHÍA credit_accounts (để bắt
    /// cả ví có reserved_credits&gt;0 mà count=0), sửa drift bằng ExecuteUpdate set
    /// <c>reserved_credits = count</c>. Guard chống âm: count luôn ≥0 nên không bao giờ set số âm
    /// (thêm CHECK <c>ck_credit_accounts_non_negative</c> ở DB1 làm lưới an toàn cuối).
    ///
    /// SCOPE = core Payment-DB thuần: reservation có sẵn owner_type/owner_id → KHÔNG cần gọi
    /// InterviewService. Phần "reservation mà session Interview đã terminal" là DB4b/out-of-scope —
    /// SettlementReconciler (Interview) đã lo re-publish event settlement để consume/release.
    ///
    /// Mirror <c>SettlementReconciler</c>: ScanInterval config-được (Reconcile:ScanIntervalSeconds),
    /// delay 30s trước lần quét đầu, try/catch quanh mỗi vòng (1 lỗi KHÔNG giết service), tạo scope
    /// riêng/lần quét cho DbContext (BackgroundService = singleton), mỗi ví bọc try/catch (chạm CHECK
    /// không giết cả vòng).
    /// </summary>
    public class CreditReservationReconciler : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ReconcileSettings _options;
        private readonly ILogger<CreditReservationReconciler> _logger;

        public CreditReservationReconciler(
            IServiceScopeFactory scopeFactory,
            IOptions<ReconcileSettings> options,
            ILogger<CreditReservationReconciler> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Chờ 1 nhịp cho app khởi động xong trước khi quét lần đầu.
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
                    // Không để 1 vòng lỗi giết cả background service.
                    _logger.LogError(ex, "Lỗi khi đối soát reserved_credits ↔ count(reservations Reserved)");
                }

                await Task.Delay(interval, ct);
            }
        }

        // private + gọi qua reflection trong test (idiom repo: SettlementReconciler/StuckAnswerRepublisher).
        private async Task ScanOnceAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return;   // safe-disable

            // BackgroundService = singleton → tạo scope riêng cho DbContext (scoped) mỗi vòng.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

            // Quét TỪ PHÍA credit_accounts (materialize trước) để bắt cả ví reserved_credits>0 mà count=0.
            var accounts = await db.CreditAccounts
                .Select(a => new AccountSnapshot(a.Id, a.OwnerType, a.OwnerId, a.ReservedCredits))
                .ToListAsync(ct);

            var fixedCount = 0;
            foreach (var a in accounts)
            {
                // CountAsync per account (KHÔNG GroupBy — tránh rủi ro dịch SQL, chưa dùng ở Payment).
                //
                // F8 — CHỈ đếm chỗ giữ do CREDIT tài trợ. Chỗ giữ của người có thuê bao cố ý không cộng
                // reserved_credits (xem CreditAccountService.ReserveAsync §gate unlimited), nên nếu đếm
                // cả chúng thì reconciler sẽ thấy "drift" ở MỌI subscriber đang thi rồi bơm
                // reserved_credits lên — phá đúng bất biến remaining+reserved=Σdelta mà nó sinh ra để bảo
                // vệ; consume/release sau đó trừ reserved xuống âm → nổ CHECK ck_credit_accounts_
                // non_negative → tx rollback → reservation kẹt Reserved → nack-requeue vô hạn.
                // Bỏ vế funded_by này = tái tạo nguyên xi lỗi DB21 qua một cửa khác.
                var count = await db.CreditReservations.CountAsync(
                    r => r.OwnerType == a.OwnerType
                         && r.OwnerId == a.OwnerId
                         && r.Status == ReservationStatus.Reserved
                         && r.FundedBy == ReservationFunding.Credit, ct);

                if (a.ReservedCredits == count) continue;   // đã khớp → bỏ qua (idempotent)

                try
                {
                    // DB21 — CAS: chỉ ghi khi reserved_credits VẪN đúng bằng giá trị đã đọc ở snapshot.
                    // Trước đây WHERE chỉ có Id (không transaction, không guard) → nếu một ReserveAsync
                    // commit xen giữa CountAsync và ExecuteUpdate thì reconciler ghi đè count CŨ, XOÁ mất
                    // slot vừa giữ; prepaid đã trừ remaining_credits rồi ⇒ credit bốc hơi khỏi CẢ HAI cột
                    // mà không có dòng ledger nào. Consume sau đó trừ reserved xuống âm → CHECK
                    // ck_credit_accounts_non_negative nổ → tx rollback → reservation kẹt Reserved → event
                    // redeliver vô hạn (nuôi đúng cái poison-message DB22 phải dọn).
                    // Tức là: job sinh ra để SỬA drift lại chính là thứ TẠO RA drift.
                    // count ≥ 0 → không bao giờ set âm (guard chống âm + CHECK DB làm lưới cuối).
                    var updated = await db.CreditAccounts
                        .Where(x => x.Id == a.Id && x.ReservedCredits == a.ReservedCredits)
                        .ExecuteUpdateAsync(u => u
                            .SetProperty(x => x.ReservedCredits, count)
                            .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);

                    if (updated == 0)
                    {
                        // Có reserve/consume/release chen vào giữa → snapshot đã cũ. BỎ QUA vòng này,
                        // vòng sau đọc lại state mới. Idempotent: không ghi mù, không mất credit.
                        _logger.LogDebug(
                            "Bỏ qua reconcile ví {Account}: reserved_credits đã đổi giữa chừng (đọc {Observed}), thử lại vòng sau.",
                            a.Id, a.ReservedCredits);
                        continue;
                    }

                    fixedCount++;
                    _logger.LogWarning(
                        "Reconcile ví {Account} ({OwnerType}:{OwnerId}): reserved_credits {Old} → {New}",
                        a.Id, a.OwnerType, a.OwnerId, a.ReservedCredits, count);
                }
                catch (Exception ex)
                {
                    // Chạm CHECK/lỗi ghi 1 ví → bỏ ví đó, KHÔNG giết cả vòng (các ví khác vẫn được sửa).
                    _logger.LogError(ex,
                        "Không thể reconcile ví {Account} ({OwnerType}:{OwnerId}), bỏ qua vòng này",
                        a.Id, a.OwnerType, a.OwnerId);
                }
            }

            if (fixedCount > 0)
                _logger.LogInformation("Reconcile reserved_credits xong: đã sửa drift {Count} ví", fixedCount);
        }

        private readonly record struct AccountSnapshot(Guid Id, OwnerType OwnerType, Guid OwnerId, int ReservedCredits);
    }
}
