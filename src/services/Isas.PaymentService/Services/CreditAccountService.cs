using Isas.PaymentService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    // PONR1 Risk④ — snapshot tối thiểu để CreditEventHandler (E7) quyết định release hay không, mà
    // KHÔNG cần biết ReservationStatus/CreditReservation (giữ CreditEventHandler không phải thêm
    // using Isas.PaymentService.Models). IsStillReserved thay vì trả nguyên enum Status: chỉ đúng MỘT
    // câu hỏi E7 cần trả lời ("có còn Reserved để release không"), không rò thêm chi tiết ra ngoài.
    public sealed record ReservationGateSnapshot(bool IsStillReserved, DateTime CreatedAt);
    public class CreditAccountService : ICreditAccountService
    {
        private readonly PaymentDbContext _db;
        private readonly ILogger<CreditAccountService>? _logger;
        private readonly BillingSettings _billing;
        private readonly ISubscriptionService? _subscriptions;
        private readonly EntitlementResolver? _entitlements;
        private readonly TieringSettings _tiering;

        // DB22 — logger inject OPTIONAL (mẫu AI4 `ICampaignSessionClient`): ctor 1-tham-số đang được
        // gọi ở rất nhiều site test; thêm dependency bắt buộc sẽ phải sửa toàn bộ mà không đem lại gì.
        // F7 — billing options cũng OPTIONAL vì lý do y hệt; null → mặc định của BillingSettings
        // (FreeTrialCredits = 3), tức test không khai gì vẫn thấy đúng hành vi production.
        // F8 — subscription service cũng OPTIONAL vì lý do y hệt; null → coi như KHÔNG ai có thuê bao,
        // tức mọi test cũ giữ nguyên hành vi credit thuần.
        public CreditAccountService(
            PaymentDbContext db,
            ILogger<CreditAccountService>? logger = null,
            IOptions<BillingSettings>? billing = null,
            ISubscriptionService? subscriptions = null,
            EntitlementResolver? entitlements = null,
            IOptions<TieringSettings>? tiering = null)
        {
            _db = db;
            _logger = logger;
            _billing = billing?.Value ?? new BillingSettings();
            _subscriptions = subscriptions;
            _entitlements = entitlements;
            _tiering = tiering?.Value ?? new TieringSettings();
        }

        /// <summary>
        /// DB22 — cảnh báo khi bút toán trừ <c>reserved_credits</c> KHÔNG khớp row nào.
        /// Nghĩa là ví đã drift (reserved_credits &lt; số reservation Reserved thật). Ta CỐ Ý không ném:
        /// transition reservation ở trên đã commit-guard đúng 1 lần, ném ở đây chỉ khiến tx rollback →
        /// reservation kẹt <c>Reserved</c> → consumer nack-requeue vô hạn → chặn cả queue credit.
        /// Bỏ qua + log; <c>CreditReservationReconciler</c> (DB4/DB21) sẽ kéo reserved_credits về đúng.
        /// </summary>
        private void WarnIfNoAccountRow(int rows, OwnerType ownerType, Guid ownerId, string op)
        {
            if (rows > 0) return;
            _logger?.LogWarning(
                "DB22 — {Op}: ví {OwnerType}:{OwnerId} có reserved_credits=0 (drift) nên bỏ qua bút toán trừ. " +
                "Reservation vẫn chuyển trạng thái đúng; reconciler sẽ đồng bộ lại.",
                op, ownerType, ownerId);
        }

        /// <summary>
        /// F7 — suất dùng thử tặng lúc TẠO ví, chỉ cho <see cref="OwnerType.User"/> (B2C).
        /// <c>0</c> khi tắt bằng cấu hình hoặc khi chủ ví là Org (B2B đi ví Org, không dùng thử — BC-1).
        /// </summary>
        private int FreeTrialGrantFor(OwnerType ownerType) =>
            ownerType == OwnerType.User && _billing.FreeTrialCredits > 0
                ? _billing.FreeTrialCredits
                : 0;

        /// <summary>
        /// Tạo ví rỗng cho chủ sở hữu. Gọi từ ĐÚNG 2 chỗ: webhook Paid lần mua đầu
        /// (<c>WebhookService</c>) và lần reserve đầu tiên của một User (F7, <see cref="ReserveAsync"/>).
        ///
        /// F7 — suất dùng thử được cấp NGAY TRONG câu INSERT này (cả số dư lẫn bút toán sổ cái, cùng một
        /// <c>SaveChangesAsync</c>). CỐ Ý không tách thành một UPDATE tiếp sau: hai đường gọi trên đều
        /// chạy đua được, bên thua nuốt <c>DbUpdateException</c> rồi đọc lại ví — nếu phần cấp nằm ở
        /// bước sau thì bên thua sẽ cấp LẦN HAI cho ví đã có quà (credit tặng vô hạn).
        /// Chính UNIQUE(owner_type, owner_id) là thứ bảo đảm "một ví = một suất dùng thử".
        /// </summary>
        public async Task<CreditAccount> CreateAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            var exists = await _db.CreditAccounts
                .AnyAsync(x => x.OwnerType == ownerType && x.OwnerId == ownerId, ct);

            if (exists)
                throw new InvalidOperationException($"Credit account already exists for {ownerType}:{ownerId}.");

            var grant = FreeTrialGrantFor(ownerType);

            var account = new CreditAccount
            {
                Id = Guid.NewGuid(),
                OwnerType = ownerType,
                OwnerId = ownerId,
                PaymentMode = PaymentMode.Prepaid,
                Status = CreditAccountStatus.Active,
                RemainingCredits = grant,
                ReservedCredits = 0,
                FreeCreditsGranted = grant,
                UpdatedAt = DateTime.UtcNow
            };

            _db.CreditAccounts.Add(account);

            // Ghi sổ phần được tặng: credit tặng nằm chung `remaining_credits` với credit khách trả tiền,
            // nên nếu KHÔNG có bút toán này thì bất biến `remaining + reserved = Σ delta` gãy ngay từ lúc
            // tạo ví ⇒ mất luôn cái máy dò drift số dư. `delta <> 0` (CHECK DB1) nên chỉ ghi khi grant > 0.
            if (grant > 0)
            {
                _db.CreditTransactions.Add(new CreditTransaction
                {
                    Id = Guid.NewGuid(),
                    OwnerType = ownerType,
                    OwnerId = ownerId,
                    OrderId = null,   // không phát sinh từ đơn hàng
                    SessionId = null, // không gắn buổi nào
                    Delta = grant,
                    Reason = CreditTransactionReason.FreeGrant,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync(ct);

            return account;
        }

        public async Task<CreditAccount?> GetAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            return await _db.CreditAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OwnerType == ownerType && x.OwnerId == ownerId, ct);
        }

        /// <summary>
        /// F7 — bảo đảm ví tồn tại cho chủ ví ĐƯỢC HƯỞNG suất dùng thử (User, và cấu hình chưa tắt),
        /// để lần reserve đầu tiên của người mới đăng ký không rơi vào nhánh no-wallet → 402.
        ///
        /// Không làm gì khi: chủ ví là Org · suất dùng thử bị tắt (<c>Billing:FreeTrialCredits = 0</c>) ·
        /// ví đã tồn tại (⇒ KHÔNG bao giờ top-up ví cũ — đó sẽ là đường tặng credit vô hạn).
        /// Race hai request cùng tạo ví → bên thua nuốt <c>DbUpdateException</c> (UNIQUE owner) và bỏ qua:
        /// ví đã tồn tại là đủ, và suất dùng thử đã được bên thắng cấp đúng một lần.
        /// </summary>
        private Task EnsureTrialWalletAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct) =>
            FreeTrialGrantFor(ownerType) == 0
                ? Task.CompletedTask
                : EnsureWalletAsync(ownerType, ownerId, ct);

        /// <summary>
        /// Tạo ví nếu chưa có (không bao giờ top-up ví cũ). F8 dùng cho chủ ví CÓ THUÊ BAO: FK composite
        /// (owner_type, owner_id) trên <c>credit_reservations</c> cấm chỗ giữ mồ côi, nên người mua gói
        /// tháng vẫn phải có một row ví — dù kỳ hạn của họ không tiêu credit nào.
        /// </summary>
        private async Task EnsureWalletAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct)
        {
            var exists = await _db.CreditAccounts
                .AnyAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId, ct);
            if (exists) return;

            try
            {
                await CreateAccountAsync(ownerType, ownerId, ct);
            }
            // S11-CATCH — hậu kiểm provider-agnostic thay cho "nuốt mọi lỗi ghi". CỐ Ý không lọc bằng
            // PostgresException{SqlState}: bộ lọc đó luôn false trên SQLite ⇒ nhánh sẽ không bao giờ
            // được test chạm tới. Bắt cả InvalidOperationException vì CreateAccountAsync check-then-act.
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                // Dọn TOÀN BỘ tracker: CreateAccountAsync Add hai entity (ví + bút toán FreeGrant của F7).
                // Bỏ sót dòng FreeGrant thì SaveChanges sau đó chèn nó thật ⇒ sổ cái +3 mà số dư đứng yên.
                _db.ChangeTracker.Clear();

                if (await _db.CreditAccounts.AnyAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId, ct))
                    return;     // đúng là đua: ví đã có, đi tiếp

                // ⚠ BẤT ĐỐI XỨNG CÓ CHỦ ĐÍCH — chỗ này KHÔNG `throw;`, khác hai site webhook.
                // Hàm này chạy NGOÀI transaction, trên đường nóng nhất hệ thống (ReserveAsync), và đã có
                // sẵn hàng rào hạ cấp mềm ngay phía sau: reserve đọc ví thấy null ⇒ trả Insufficient
                // (402, ReserveAsync guard no-wallet). Ném ở đây chỉ đổi một cái 402 nói đúng sự thật
                // ("chưa có ví, không giữ được credit") lấy một cái 500 trên đúng đường tạo buổi thi.
                // Lỗi KHÔNG bị giấu: log ở mức Error kèm nguyên exception.
                _logger?.LogError(ex,
                    "S11-CATCH — không tạo được ví {OwnerType}:{OwnerId} và ví cũng KHÔNG tồn tại sau đó. " +
                    "Lời gọi reserve sẽ hạ cấp thành Insufficient (402), không phải 500.",
                    ownerType, ownerId);
            }
        }

        // P4/P8a — Reserve 1 credit (D7 · payment.md §Kế toán remaining↔reserved / POSTPAID).
        // Prepaid (P4): remaining−1, reserved+1 WHERE remaining≥1. Postpaid (P8a): dồn nợ tới hạn mức —
        // chỉ reserved+1 WHERE period_usage+reserved+1≤credit_limit (KHÔNG trừ remaining).
        // Ràng buộc chung: idempotent theo session_id (PAY-4), atomic chống double-spend (PAY-5),
        // hết credit / chạm hạn mức → Insufficient (402) KHÔNG để lại reservation dư.
        public async Task<ReserveResult> ReserveAsync(OwnerType ownerType, Guid ownerId, Guid sessionId, CancellationToken ct = default)
        {
            // Idempotency fast-path (PAY-4): session đã có reservation → không giữ thêm, trả về nguyên trạng.
            // UNIQUE(session_id) là "khoá" idempotency; đây chỉ là đường tắt cho lần gọi lặp tuần tự.
            var existing = await _db.CreditReservations.AsNoTracking()
                .FirstOrDefaultAsync(r => r.SessionId == sessionId, ct);
            if (existing is not null)
                return ReserveResult.AlreadyReserved(existing.Id, await ReservedCreditsOf(existing.OwnerType, existing.OwnerId, ct));

            // F7 — user B2C chưa từng mua thì CHƯA có ví (ví vốn chỉ tạo lazy ở webhook Paid), nên trước
            // F7 buổi luyện đầu tiên của người mới đăng ký luôn rơi vào nhánh no-wallet → 402. Tạo ví ở
            // đây để suất dùng thử (cấp bên trong CreateAccountAsync) đến được đúng nhóm đó.
            //
            // CỐ Ý nằm NGOÀI transaction reserve bên dưới: ví đã cấp là một sự thật độc lập, không có lý
            // do gì roll back nó khi reserve fail; và tránh giữ khoá UNIQUE(owner) trên credit_accounts
            // xuyên nhiều roundtrip ngay giữa đường nóng nhất của hệ thống.
            //
            // Org KHÔNG đi đường này: ví Org do OrgAdmin mua credit tạo ra, không có suất dùng thử (BC-1)
            // ⇒ Org chưa có ví vẫn 402 y như trước.
            await EnsureTrialWalletAsync(ownerType, ownerId, ct);

            // ── F8 — GATE UNLIMITED ───────────────────────────────────────────────────────────────
            // Chủ ví còn thuê bao ⇒ chỗ giữ này KHÔNG do credit tài trợ. Quyết định được chốt DUY NHẤT
            // ở đây rồi ghi cứng vào `funded_by` của reservation (xem CreditReservation.FundedBy).
            //
            // BẤT BIẾN SỔ CÁI được giữ bằng cách KHÔNG ĐỘNG VÀO GÌ CẢ, chứ không phải bằng bút toán bù:
            //   `remaining_credits + reserved_credits = Σ credit_transactions.delta`
            // Chỗ giữ kiểu Subscription không đổi remaining, không đổi reserved, không ghi ledger ⇒ cả
            // hai vế đứng yên qua CẢ BA bước reserve/consume/release. Mọi phương án khác đều hỏng:
            //   • "reserved+1 rồi thôi" → vế trái tăng, vế phải đứng yên ⇒ bất biến gãy ngay từ reserve;
            //   • "ghi ledger +1 rồi consume −1" → đúc credit khống vào sổ, và +1/−1 lệch nhịp thì số dư
            //     thật của người dùng bị bơm lên;
            //   • "vẫn trừ remaining nhưng hoàn lại sau" → thuê bao mà vẫn 402 khi ví rỗng = mất tính năng.
            //
            // Hệ quả BẮT BUỘC đi kèm: bất biến thứ hai (DB4/DB21)
            //   `reserved_credits = count(reservations WHERE status=Reserved)`
            // phải thu hẹp thành `... AND funded_by='Credit'` — nếu không, CreditReservationReconciler sẽ
            // đếm cả chỗ giữ của subscriber rồi bơm reserved_credits lên, phá bất biến thứ nhất. Đó đúng
            // là lớp bug DB21 (job sửa drift lại tự tạo drift), nên nó được khoá bằng test riêng.
            // Tiering rollout phải giữ nguyên F8 khi cờ tắt. Khi cờ bật, chỉ snapshot entitlement
            // quyết định funding: B2B Credit vẫn đi qua ví, không được biến thành unlimited.
            var entitlement = _tiering.Enabled && _entitlements is not null
                ? await _entitlements.ResolveAsync(ownerType, ownerId, ct)
                : null;
            var metered = entitlement is { InterviewFunding: InterviewFunding.Metered, SubscriptionId: not null };
            var subsidized = _tiering.Enabled
                ? entitlement is { InterviewFunding: InterviewFunding.Unlimited, SubscriptionId: not null }
                : _subscriptions is not null && await _subscriptions.HasActiveAsync(ownerType, ownerId, ct);

            // Người mua gói tháng có thể chưa từng mua credit ⇒ chưa có ví, mà FK composite trên
            // credit_reservations lại đòi ví phải tồn tại. Tạo ví rỗng ở đây (Org: 0 credit, không bút
            // toán; User: đi qua đúng đường F7 nên vẫn được suất dùng thử của mình).
            if (subsidized || metered) await EnsureWalletAsync(ownerType, ownerId, ct);

            // DB25b — bọc IExecutionStrategy vì Npgsql bật EnableRetryOnFailure: chiến lược retry
            // TỪ CHỐI transaction do người dùng tự mở, và khi chạy lại delegate nó KHÔNG reset change
            // tracker (chi tiết + hệ quả với sổ cái: xem <see cref="DbRetry"/>).
            return await DbRetry.RunAsync(_db, async Task<ReserveResult> () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // DB9 — FK composite (owner_type,owner_id)→credit_accounts CẤM chèn reservation mồ côi. Trước đây
                // no-wallet → chèn reservation rồi ExecuteUpdate 0 row → rollback → 402 (reservation mồ côi
                // transient); nay FK chặn NGAY lúc chèn (SaveChanges ném FK) → sẽ bị catch nhầm là race
                // UNIQUE(session_id) rồi FirstAsync ném (không có row) → 500. Giữ NGUYÊN hành vi PAY-5
                // (no-wallet→402, KHÔNG để lại reservation): đọc ví TRƯỚC — chưa có ví → Insufficient ngay
                // (không chèn). Đọc account đây cũng để CHỌN nhánh bút toán (prepaid trừ remaining P4 · postpaid
                // dồn nợ tới credit_limit P8a); guard atomic đầy đủ vẫn ở WHERE ExecuteUpdate (gồm payment_mode)
                // → không phá chống double-spend.
                var acc = await _db.CreditAccounts.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId, ct);
                if (acc is null)
                {
                    await tx.RollbackAsync(ct);
                    return ReserveResult.Insufficient();
                }

                // Chèn reservation TRƯỚC khi trừ số dư: UNIQUE(session_id) chặn 2 request cùng session
                // cùng trừ credit (double-spend qua race idempotency). Chỉ request thắng insert mới trừ ví.
                var reservation = new CreditReservation
                {
                    Id = Guid.NewGuid(),
                    OwnerType = ownerType,
                    OwnerId = ownerId,
                    SessionId = sessionId,
                    Status = ReservationStatus.Reserved,
                    FundedBy = metered ? ReservationFunding.SubscriptionMetered : subsidized ? ReservationFunding.Subscription : ReservationFunding.Credit,
                    MeteredSubscriptionId = metered ? entitlement!.SubscriptionId : null,
                    MeteredPeriodStart = metered ? MeteredPeriodStart(DateTime.UtcNow, entitlement!.MeterAnchorDay) : null,
                    PaymentMode = acc.PaymentMode,
                    CreatedAt = DateTime.UtcNow
                };
                _db.CreditReservations.Add(reservation);

                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                // S11-CATCH — hậu kiểm: rollback + dọn tracker rồi HỎI LẠI xem session này có THẬT SỰ đã
                // được request khác giữ chỗ chưa. Bản cũ `catch` trần + `FirstAsync` biến MỌI lỗi ghi
                // (FK composite owner→credit_accounts DB9, CHECK enum/metered, varchar quá dài, lỗi tạm
                // thời) thành "Sequence contains no elements" — che mất nguyên nhân thật ở đúng bước tạo
                // buổi thi của cả B2C lẫn B2B, và nuốt luôn lỗi tạm thời trước khi execution strategy
                // (EnableRetryOnFailure) kịp nhìn thấy để thử lại.
                catch (DbUpdateException)
                {
                    await tx.RollbackAsync(ct);
                    _db.ChangeTracker.Clear();

                    var raced = await _db.CreditReservations.AsNoTracking()
                        .FirstOrDefaultAsync(r => r.SessionId == sessionId, ct);

                    // Không có chỗ giữ nào cho session này ⇒ KHÔNG phải đua UNIQUE(session_id) ⇒ lỗi thật.
                    if (raced is null) throw;

                    return ReserveResult.AlreadyReserved(raced.Id, await ReservedCreditsOf(raced.OwnerType, raced.OwnerId, ct));
                }

                // acc đã đọc ở trên (guard no-wallet + chọn nhánh bút toán). Guard đầy đủ vẫn nằm trong WHERE
                // của ExecuteUpdate (atomic self-consistent, gồm cả payment_mode) → không phá chống double-spend.
                int rows = 0;
                if (reservation.FundedBy == ReservationFunding.SubscriptionMetered)
                {
                    // One row per subscription + UTC calendar month. INSERT is idempotent for competing
                    // reserves; the guarded UPDATE is the quota gate and is in this same transaction.
                    await _db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO subscription_meters (subscription_id, period_start, used_count, reserved_count, updated_at)
                        VALUES ({reservation.MeteredSubscriptionId!.Value}, {reservation.MeteredPeriodStart!.Value}, 0, 0, {DateTime.UtcNow})
                        ON CONFLICT (subscription_id, period_start) DO UPDATE
                        SET updated_at = EXCLUDED.updated_at", ct);
                    rows = await _db.SubscriptionMeters
                        .Where(m => m.SubscriptionId == reservation.MeteredSubscriptionId!.Value
                                 && m.PeriodStart == reservation.MeteredPeriodStart!.Value
                                 && _db.CreditAccounts.Any(a => a.OwnerType == ownerType
                                                              && a.OwnerId == ownerId
                                                              && a.Status == CreditAccountStatus.Active)
                                 && m.UsedCount + m.ReservedCount + 1 <= entitlement!.MonthlyQuota)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.ReservedCount, m => m.ReservedCount + 1)
                            .SetProperty(m => m.UpdatedAt, _ => DateTime.UtcNow), ct);
                    if (rows == 0)
                    {
                        // Quota exhausted: the meter update did not mutate, so this reservation can
                        // atomically fall through to the normal credit/postpaid path.
                        reservation.FundedBy = ReservationFunding.Credit;
                        reservation.MeteredSubscriptionId = null;
                        reservation.MeteredPeriodStart = null;
                        await _db.CreditReservations.Where(r => r.Id == reservation.Id)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(r => r.FundedBy, ReservationFunding.Credit)
                                .SetProperty(r => r.MeteredSubscriptionId, (Guid?)null)
                                .SetProperty(r => r.MeteredPeriodStart, (DateTime?)null)
                                .SetProperty(r => r.UpdatedAt, _ => DateTime.UtcNow), ct);
                    }
                }
                if (reservation.FundedBy == ReservationFunding.Subscription)
                {
                    // KHÔNG bút toán số dư (xem giải thích bất biến ở gate phía trên). Vẫn phải qua một câu
                    // UPDATE có điều kiện vì cần guard ATOMIC `status = Active`: ví bị Đình chỉ thì chặn hành
                    // động MỚI (PAY-12) — thuê bao không mua được quyền đi vòng qua lệnh đình chỉ. Câu này chỉ
                    // chạm updated_at nên số dư đứng yên tuyệt đối; 0 row = ví Suspended/biến mất ⇒ 402.
                    //
                    // KHÔNG áp guard Overdue của postpaid ở đây: hoá đơn quá hạn là nợ theo LƯỢT TIÊU THỤ
                    // (period_usage), mà chỗ giữ kiểu thuê bao không sinh lượt tính tiền nào.
                    rows = await _db.CreditAccounts
                        .Where(a => a.OwnerType == ownerType
                                    && a.OwnerId == ownerId
                                    && a.Status == CreditAccountStatus.Active)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);
                }
                else if (reservation.FundedBy == ReservationFunding.Credit && acc.PaymentMode == PaymentMode.Postpaid)
                {
                    // BK17 — Overdue-block: ví Org còn hóa đơn Overdue (nợ kỳ trước chưa tất toán) → chặn reserve
                    // MỚI (payment.md:379/431 "không có hóa đơn Overdue"; §State machine "Overdue ⇒ chặn reserve
                    // mới, KHÔNG văng in-flight"). Đọc trong CÙNG transaction; reservation vừa chèn ở trên →
                    // rollback gỡ luôn ⇒ no orphan (PAY-5). Idempotency vẫn do UNIQUE(session_id) bảo đảm.
                    var hasOverdue = await _db.Invoices
                        .AnyAsync(i => i.OwnerType == ownerType
                                    && i.OwnerId == ownerId
                                    && i.Status == InvoiceStatus.Overdue, ct);
                    if (hasOverdue)
                    {
                        await tx.RollbackAsync(ct);
                        return ReserveResult.Insufficient();
                    }

                    // POSTPAID (payment.md §Kế toán): KHÔNG trừ remaining (postpaid remaining=0), chỉ reserved+1;
                    // guard ATOMIC period_usage + reserved + 1 ≤ credit_limit → 0 row = chạm hạn mức ⇒ 402
                    // (PAY-5, no orphan). period_usage CHỈ tăng khi Consume (P5/P8b) — reserve KHÔNG dồn nợ kỳ
                    // (bỏ ngang/release → không tính nợ). credit_limit chưa đặt (NULL) ⇒ so sánh NULL loại row ⇒
                    // 402 (postpaid cần PlatformAdmin đặt hạn mức mới reserve được).
                    rows = await _db.CreditAccounts
                        .Where(a => a.OwnerType == ownerType
                                    && a.OwnerId == ownerId
                                    && a.PaymentMode == PaymentMode.Postpaid
                                    && a.Status == CreditAccountStatus.Active
                                    && (a.PeriodUsage ?? 0) + a.ReservedCredits + 1 <= a.CreditLimit)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.ReservedCredits, a => a.ReservedCredits + 1)
                            .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);
                }
                else if (reservation.FundedBy == ReservationFunding.Credit)
                {
                    // PREPAID (giữ nguyên P4): 1 câu UPDATE có điều kiện (không đọc-rồi-ghi rời) → 2 reserve song
                    // song không cùng vượt check remaining≥1 ⇒ chống double-spend (PAY-5). 0 row = hết credit /
                    // không có ví / account Suspended.
                    rows = await _db.CreditAccounts
                        .Where(a => a.OwnerType == ownerType
                                    && a.OwnerId == ownerId
                                    && a.PaymentMode == PaymentMode.Prepaid
                                    && a.Status == CreditAccountStatus.Active
                                    && a.RemainingCredits >= 1)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.RemainingCredits, a => a.RemainingCredits - 1)
                            .SetProperty(a => a.ReservedCredits, a => a.ReservedCredits + 1)
                            .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);
                }

                if (rows == 0)
                {
                    await tx.RollbackAsync(ct); // gỡ luôn reservation vừa chèn → KHÔNG để lại reservation dư
                    return ReserveResult.Insufficient();
                }

                await tx.CommitAsync(ct);

                var reserved = await ReservedCreditsOf(ownerType, ownerId, ct);
                return ReserveResult.Reserved(reservation.Id, reserved);
            });
        }

        private Task<int> ReservedCreditsOf(OwnerType ownerType, Guid ownerId, CancellationToken ct) =>
            _db.CreditAccounts.AsNoTracking()
                .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
                .Select(a => a.ReservedCredits)
                .FirstOrDefaultAsync(ct);

        internal static DateTime MeteredPeriodStart(DateTime now, short? anchorDay)
        {
            var anchor = Math.Clamp(anchorDay ?? 1, (short)1, (short)28);
            var monthAnchor = new DateTime(now.Year, now.Month, anchor, 0, 0, 0, DateTimeKind.Utc);
            return now < monthAnchor ? monthAnchor.AddMonths(-1) : monthAnchor;
        }

        // P5 — Consume 1 credit khi SessionScored (D7 · payment.md §State machine "credit_reservations" +
        // "kế toán remaining↔reserved"). Reservation Reserved→Consumed + reserved−1 + ledger(Consume,−1);
        // remaining KHÔNG đổi (credit đã tiêu thật qua bút toán −1). Idempotent/absorbing theo session_id
        // (PAY-11): Consumed/Released đã tới trước → no-op; chưa có reservation → no-op (KHÔNG trừ oan).
        public async Task<ConsumeResult> ConsumeAsync(Guid sessionId, CancellationToken ct = default)
        {
            // Chủ ví lấy từ reservation (nguồn chân lý — dựng lúc reserve), không tin owner request.
            var reservation = await _db.CreditReservations.AsNoTracking()
                .FirstOrDefaultAsync(r => r.SessionId == sessionId, ct);

            // Miss event reserve (reservation chưa tồn tại): reserve là điều kiện vào bài → thiếu = bất
            // thường; KHÔNG âm thầm trừ, no-op an toàn, controller trả 200 (§State machine payment.md).
            if (reservation is null)
                return ConsumeResult.NoReservation();

            // Absorbing (PAY-11): đã Consumed hoặc Released → bỏ qua, KHÔNG trừ lần 2 / không trừ oan.
            if (reservation.Status != ReservationStatus.Reserved)
                return ConsumeResult.AlreadyFinalized(reservation.Id);

            // DB25b — bọc IExecutionStrategy vì Npgsql bật EnableRetryOnFailure: chiến lược retry
            // TỪ CHỐI transaction do người dùng tự mở, và khi chạy lại delegate nó KHÔNG reset change
            // tracker (chi tiết + hệ quả với sổ cái: xem <see cref="DbRetry"/>).
            return await DbRetry.RunAsync(_db, async Task<ConsumeResult> () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // Transition ATOMIC có guard WHERE status=Reserved: 2 consume song song cùng session → chỉ 1
                // thắng (1 row) → chỉ 1 bút toán (idempotent, chống double-process). 0 row = ai đó vừa
                // consume/release trước → hấp thụ, no-op.
                var moved = await _db.CreditReservations
                    .Where(r => r.SessionId == sessionId && r.Status == ReservationStatus.Reserved)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, ReservationStatus.Consumed)
                        // DB14 — ExecuteUpdate bỏ qua SaveChanges override → stamp updated_at tường minh.
                        .SetProperty(r => r.UpdatedAt, _ => DateTime.UtcNow), ct);

                if (moved == 0)
                {
                    await tx.RollbackAsync(ct);
                    var raced = await _db.CreditReservations.AsNoTracking()
                        .FirstAsync(r => r.SessionId == sessionId, ct);
                    return ConsumeResult.AlreadyFinalized(raced.Id);
                }

                // F8 — chỗ giữ do THUÊ BAO tài trợ: reserve đã không trừ gì thì consume cũng không trừ gì.
                // Chỉ chuyển trạng thái reservation (đã làm ở trên) rồi commit. KHÔNG ghi credit_transactions:
                // delta sẽ phải bằng 0 (không có credit nào bị tiêu) → vi phạm CHECK ck_credit_transactions_
                // delta_nonzero → SaveChanges ném → rollback → reservation kẹt Reserved → consumer nack-requeue
                // vô hạn → nghẽn queue credit. Đó chính xác là hình dạng lỗi DB20/DB22, chỉ khác điểm vào.
                // Vết tiêu thụ của subscriber KHÔNG mất: nó nằm ở chính row reservation (Consumed + session_id).
                if (reservation.FundedBy == ReservationFunding.SubscriptionMetered)
                {
                    var rows = await _db.SubscriptionMeters
                        .Where(m => m.SubscriptionId == reservation.MeteredSubscriptionId!.Value
                                 && m.PeriodStart == reservation.MeteredPeriodStart!.Value
                                 && m.ReservedCount >= 1)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.ReservedCount, m => m.ReservedCount - 1)
                            .SetProperty(m => m.UsedCount, m => m.UsedCount + 1)
                            .SetProperty(m => m.UpdatedAt, _ => DateTime.UtcNow), ct);
                    if (rows == 0) _logger?.LogWarning("Meter snapshot missing/drifted while consuming {SessionId}", sessionId);
                    await tx.CommitAsync(ct);
                    return ConsumeResult.Consumed(reservation.Id);
                }
                if (reservation.FundedBy == ReservationFunding.Subscription)
                {
                    await tx.CommitAsync(ct);
                    return ConsumeResult.Consumed(reservation.Id);
                }

                // reserved−1 (nhả chỗ giữ). remaining KHÔNG đổi → credit "tiêu" thật thể hiện ở ledger −1.
                // (bất biến audit prepaid: remaining + reserved = Σ credit_transactions.delta vẫn giữ.)
                // POSTPAID (BK7 · payment.md §Kế toán POSTPAID): consume dồn nợ kỳ → period_usage += 1 (nguồn
                // snapshot ra invoice.interview_count cuối kỳ); reserve KHÔNG cộng (P8a) nên nợ chỉ tính khi tiêu
                // thật (bỏ ngang/release không dồn nợ). Đọc payment_mode rời chỉ để CHỌN nhánh — increment
                // period_usage là SQL self-referential (atomic); transition Reserved→Consumed ở trên (guard
                // WHERE status=Reserved) đã bảo đảm đúng 1 consume/session ⇒ KHÔNG cộng nợ oan (idempotent PAY-11).

                //var isPostpaid = await _db.CreditAccounts.AsNoTracking()
                //    .Where(a => a.OwnerType == reservation.OwnerType && a.OwnerId == reservation.OwnerId)
                //    .Select(a => a.PaymentMode)
                //    .FirstOrDefaultAsync(ct) == PaymentMode.Postpaid;
                var isPostpaid = reservation.PaymentMode == PaymentMode.Postpaid;

                // DB22 — guard `ReservedCredits >= 1` trong WHERE (trước đây chỉ lọc theo owner). Nếu ví đã
                // drift về 0 thì bút toán cũ trừ xuống âm → vi phạm CHECK ck_credit_accounts_non_negative →
                // ném → tx rollback → reservation kẹt Reserved → consumer nack-requeue vô hạn → CHẶN CẢ QUEUE
                // credit (poison message). Guard biến "nổ CHECK" thành "0 row + log" — mất mát tối đa là
                // reserved_credits lệch, thứ mà reconciler DB4/DB21 vốn sinh ra để sửa.
                int accRows;
                if (isPostpaid)
                {
                    accRows = await _db.CreditAccounts
                        .Where(a => a.OwnerType == reservation.OwnerType && a.OwnerId == reservation.OwnerId
                                    && a.ReservedCredits >= 1)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.ReservedCredits, a => a.ReservedCredits - 1)
                            .SetProperty(a => a.PeriodUsage, a => (int?)((a.PeriodUsage ?? 0) + 1))
                            .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);
                }
                else
                {
                    // PREPAID giữ nguyên (không có period_usage): chỉ reserved−1.
                    accRows = await _db.CreditAccounts
                        .Where(a => a.OwnerType == reservation.OwnerType && a.OwnerId == reservation.OwnerId
                                    && a.ReservedCredits >= 1)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.ReservedCredits, a => a.ReservedCredits - 1)
                            .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);
                }

                WarnIfNoAccountRow(accRows, reservation.OwnerType, reservation.OwnerId, "Consume");

                // Bút toán Consume −1 (sổ cái append-only). session_id ref lỏng, order_id null (không gắn order).
                _db.CreditTransactions.Add(new CreditTransaction
                {
                    Id = Guid.NewGuid(),
                    OwnerType = reservation.OwnerType,
                    OwnerId = reservation.OwnerId,
                    OrderId = null,
                    SessionId = sessionId,
                    Delta = -1,
                    Reason = CreditTransactionReason.Consume,
                    CreatedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);

                return ConsumeResult.Consumed(reservation.Id);
            });
        }

        // P6/BK11 — Release chỗ giữ khi SessionAbandoned/lỗi (D7 · payment.md §State machine "credit_reservations" +
        // "kế toán remaining↔reserved"). Reservation Reserved→Released + hoàn chỗ giữ: prepaid = reserved−1,
        // remaining+1 (P6); POSTPAID = CHỈ reserved−1 (BK11 — postpaid remaining=0, period_usage KHÔNG đổi).
        // KHÔNG ghi credit_transactions — credit đã giữ được trả lại chứ không tiêu (bảo toàn bất biến audit
        // remaining+reserved=Σledger). Idempotent/absorbing theo session_id (PAY-11): Consumed/Released đã tới
        // trước → no-op (KHÔNG hoàn oan sau khi đã tiêu); chưa có reservation → no-op (KHÔNG hoàn oan).
        public async Task<ReleaseResult> ReleaseAsync(Guid sessionId, CancellationToken ct = default)
        {
            // Chủ ví lấy từ reservation (nguồn chân lý — dựng lúc reserve), không tin owner request.
            var reservation = await _db.CreditReservations.AsNoTracking()
                .FirstOrDefaultAsync(r => r.SessionId == sessionId, ct);

            // Miss event reserve (reservation chưa tồn tại): không có chỗ giữ để hoàn → no-op an toàn,
            // controller trả 200 (§State machine payment.md). KHÔNG âm thầm cộng credit.
            if (reservation is null)
                return ReleaseResult.NoReservation();

            // Absorbing (PAY-11): đã Consumed (đã tiêu thật) → KHÔNG hoàn oan; đã Released → idempotent no-op.
            if (reservation.Status != ReservationStatus.Reserved)
                return ReleaseResult.AlreadyFinalized(reservation.Id);

            // DB25b — bọc IExecutionStrategy vì Npgsql bật EnableRetryOnFailure: chiến lược retry
            // TỪ CHỐI transaction do người dùng tự mở, và khi chạy lại delegate nó KHÔNG reset change
            // tracker (chi tiết + hệ quả với sổ cái: xem <see cref="DbRetry"/>).
            return await DbRetry.RunAsync(_db, async Task<ReleaseResult> () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // Transition ATOMIC có guard WHERE status=Reserved: consume & release song song cùng session →
                // chỉ 1 thắng (1 row) → chỉ 1 bên chuyển tiếp (chống double-process race consume↔release).
                // 0 row = ai đó vừa consume/release trước → hấp thụ, no-op (KHÔNG hoàn oan).
                var moved = await _db.CreditReservations
                    .Where(r => r.SessionId == sessionId && r.Status == ReservationStatus.Reserved)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, ReservationStatus.Released)
                        // DB14 — ExecuteUpdate bỏ qua SaveChanges override → stamp updated_at tường minh.
                        .SetProperty(r => r.UpdatedAt, _ => DateTime.UtcNow), ct);

                if (moved == 0)
                {
                    await tx.RollbackAsync(ct);
                    var raced = await _db.CreditReservations.AsNoTracking()
                        .FirstAsync(r => r.SessionId == sessionId, ct);
                    return ReleaseResult.AlreadyFinalized(raced.Id);
                }

                // F8 — chỗ giữ do THUÊ BAO tài trợ: không có gì để hoàn (reserve đã không trừ cột nào).
                // ⚠ ĐÂY LÀ CHỖ NGUY HIỂM NHẤT của cả tính năng: nếu để rơi xuống nhánh prepaid bên dưới thì
                // `remaining_credits + 1` sẽ ĐÚC RA một credit trả tiền chưa từng được mua, mỗi lần một
                // subscriber bỏ ngang buổi thi. Nhánh được chọn theo `funded_by` đã ghi cứng lúc reserve chứ
                // KHÔNG theo "hiện giờ còn thuê bao không" — nên thuê bao hết hạn giữa buổi cũng không lái
                // được release sang nhánh credit (và người đang thi không bị đụng tới — PAY-12).
                if (reservation.FundedBy == ReservationFunding.SubscriptionMetered)
                {
                    var rows = await _db.SubscriptionMeters
                        .Where(m => m.SubscriptionId == reservation.MeteredSubscriptionId!.Value
                                 && m.PeriodStart == reservation.MeteredPeriodStart!.Value
                                 && m.ReservedCount >= 1)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.ReservedCount, m => m.ReservedCount - 1)
                            .SetProperty(m => m.UpdatedAt, _ => DateTime.UtcNow), ct);
                    if (rows == 0) _logger?.LogWarning("Meter snapshot missing/drifted while releasing {SessionId}", sessionId);
                    await tx.CommitAsync(ct);
                    return ReleaseResult.Released(reservation.Id);
                }
                if (reservation.FundedBy == ReservationFunding.Subscription)
                {
                    await tx.CommitAsync(ct);
                    return ReleaseResult.Released(reservation.Id);
                }

                // Hoàn chỗ giữ — KHÔNG ghi ledger cả 2 mode (chỗ giữ được trả lại chứ không tiêu →
                // bảo toàn bất biến audit remaining+reserved=Σledger). Đọc payment_mode rời chỉ để CHỌN nhánh
                // (mẫu BK7 ConsumeAsync); transition Reserved→Released ở trên (guard WHERE status=Reserved) đã
                // bảo đảm đúng 1 release/session ⇒ KHÔNG hoàn oan (idempotent PAY-11).

                //var isPostpaid = await _db.CreditAccounts.AsNoTracking()
                //    .Where(a => a.OwnerType == reservation.OwnerType && a.OwnerId == reservation.OwnerId)
                //    .Select(a => a.PaymentMode)
                //    .FirstOrDefaultAsync(ct) == PaymentMode.Postpaid;
                var isPostpaid = reservation.PaymentMode == PaymentMode.Postpaid;

                // DB22 — guard `ReservedCredits >= 1` (xem giải thích ở ConsumeAsync).
                int accRows;
                if (isPostpaid)
                {
                    // POSTPAID (BK11 · payment.md §Kế toán POSTPAID release): CHỈ reserved−1. KHÔNG remaining+1
                    // (postpaid remaining=0, bơm 0→1 là sai); period_usage KHÔNG đổi — chỗ giữ chưa tiêu nên
                    // không phát sinh nợ kỳ (reserve postpaid không dồn nợ P8a → release cũng không).
                    accRows = await _db.CreditAccounts
                        .Where(a => a.OwnerType == reservation.OwnerType && a.OwnerId == reservation.OwnerId
                                    && a.ReservedCredits >= 1)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.ReservedCredits, a => a.ReservedCredits - 1)
                            .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);
                }
                else
                {
                    // PREPAID (giữ nguyên P6): reserved−1, remaining+1 (nghịch đảo của reserve) → tổng
                    // remaining+reserved bảo toàn.
                    accRows = await _db.CreditAccounts
                        .Where(a => a.OwnerType == reservation.OwnerType && a.OwnerId == reservation.OwnerId
                                    && a.ReservedCredits >= 1)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.ReservedCredits, a => a.ReservedCredits - 1)
                            .SetProperty(a => a.RemainingCredits, a => a.RemainingCredits + 1)
                            .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);
                }

                WarnIfNoAccountRow(accRows, reservation.OwnerType, reservation.OwnerId, "Release");

                await tx.CommitAsync(ct);

                return ReleaseResult.Released(reservation.Id);
            });
        }

        /// <summary>
        /// PONR1 Risk④ — đọc thuần trạng thái + thời điểm tạo reservation của 1 session, phục vụ
        /// CreditEventHandler (E7) quyết định gate release theo mốc cutover. KHÔNG đổi gì. null = chưa
        /// từng reserve session này (mirror Consume/ReleaseAsync: miss-reserve → caller tự quyết no-op).
        /// </summary>
        public async Task<ReservationGateSnapshot?> GetReservationGateSnapshotAsync(
            Guid sessionId, CancellationToken ct = default)
        {
            var reservation = await _db.CreditReservations.AsNoTracking()
                .FirstOrDefaultAsync(r => r.SessionId == sessionId, ct);

            return reservation is null
                ? null
                : new ReservationGateSnapshot(reservation.Status == ReservationStatus.Reserved, reservation.CreatedAt);
        }
    }
}
