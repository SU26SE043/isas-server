using Microsoft.EntityFrameworkCore;
using PaymentService.Models;
using System.Text.Json;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// F20 — cấp credit khuyến mãi (PlatformAdmin). Xem <see cref="IAdminCreditService"/> cho phạm vi.
    ///
    /// <para><b>Bất biến sổ cái.</b> <c>remaining_credits += N</c> và bút toán <c>PromoGrant +N</c> nằm
    /// trong CÙNG transaction ⇒ <c>remaining + reserved = Σ delta</c> giữ nguyên. Cộng số dư mà không ghi
    /// sổ chính là kiểu "credit không sổ sách" mà F7 đã cố ý từ chối: nó phá đúng cái máy dò drift duy
    /// nhất của hệ thống, nên credit bốc hơi sau này sẽ không ai phát hiện được.</para>
    ///
    /// <para><b>Vì sao bút toán mang Reason riêng.</b> Nếu quà đi dưới nhãn <c>Purchase</c> thì báo cáo
    /// doanh thu (F19) — vốn cộng theo đơn — vẫn đúng, NHƯNG mọi phân tích sau này đọc sổ cái sẽ tưởng
    /// quà là tiền. Còn nếu đi dưới <c>FreeGrant</c> thì phép "ví này đã dùng suất dùng thử chưa" (F7)
    /// hỏng. Ba nguồn credit = ba nhãn.</para>
    /// </summary>
    public class AdminCreditService : IAdminCreditService
    {
        private readonly PaymentDbContext _db;
        private readonly ICreditAccountService _accounts;
        private readonly ILogger<AdminCreditService>? _logger;

        public AdminCreditService(
            PaymentDbContext db, ICreditAccountService accounts,
            ILogger<AdminCreditService>? logger = null)
        {
            _db = db;
            _accounts = accounts;
            _logger = logger;
        }

        public async Task<GrantResult> GrantAsync(
            OwnerType ownerType, Guid ownerId, int credits, string? note, string? idempotencyKey, Guid adminUserId,
            CancellationToken ct = default)
        {
            idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();

            // Cấp 0 (hoặc âm) không có nghĩa, và bút toán delta = 0 vi phạm CHECK
            // ck_credit_transactions_delta_nonzero → SaveChanges ném giữa transaction. Chặn ở cửa thay vì
            // để DB ném (bài học DB20: lỗi ném từ trong tx làm hỏng cả những thứ đã làm trước đó).
            // Trừ credit thì phải đi đường hoàn tiền F18 (có bút toán đảo gắn khoản gốc), không phải
            // "cấp số âm" — nếu không thì admin có một đường trừ credit không dấu vết.
            if (credits <= 0)
                return new GrantResult(GrantOutcome.InvalidAmount, ownerType, ownerId, 0, 0, null);

            // Fast path cho retry sau khi request đầu đã commit. Đồng thời tránh tạo ví/free-trial lần nữa.
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var original = await FindOriginalGrantAsync(ownerType, ownerId, idempotencyKey, ct);
                if (original is not null)
                    return Replay(original);
            }

            // Ví chưa tồn tại (chủ ví chưa từng mua/luyện) → tạo. Đi qua CreateAccountAsync chứ không tự
            // INSERT: đó là NƠI DUY NHẤT cấp suất dùng thử F7 (PAY-14), nên tự dựng ví ở đây sẽ tạo ra
            // một ví User không có suất dùng thử — im lặng tước quyền của người vừa được tặng quà.
            // Hệ quả có chủ ý: cấp quà cho user mới thì ví sinh ra kèm cả 3 credit dùng thử.
            if (await _accounts.GetAccountAsync(ownerType, ownerId, ct) is null)
            {
                try
                {
                    await _accounts.CreateAccountAsync(ownerType, ownerId, ct);
                }
                // S11-CATCH — hậu kiểm provider-agnostic (mẫu WebhookService.EnsureWalletExistsAsync).
                // Bắt cả InvalidOperationException vì CreateAccountAsync check-then-act.
                catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
                {
                    // Dọn TOÀN BỘ tracker: CreateAccountAsync Add hai entity (ví + bút toán FreeGrant F7).
                    // Bỏ sót dòng FreeGrant thì SaveChanges của chính lệnh cấp quà bên dưới sẽ chèn nó
                    // thật ⇒ sổ cái +3 mà số dư chỉ tăng đúng phần quà ⇒ gãy bất biến số dư.
                    _db.ChangeTracker.Clear();

                    if (await _accounts.GetAccountAsync(ownerType, ownerId, ct) is null)
                        // ⚠ BẤT ĐỐI XỨNG CÓ CHỦ ĐÍCH — KHÔNG `throw;`. Hàm chạy NGOÀI transaction và đã có
                        // hàng rào hạ cấp mềm ngay sau: câu cộng ATOMIC bên dưới khớp 0 row ⇒ trả
                        // GrantOutcome.WalletMissing (400/409 ở controller) thay vì 500. Ném ở đây chỉ đổi
                        // một câu trả lời đúng sự thật lấy một stack trace. Lỗi KHÔNG bị giấu — log Error.
                        _logger?.LogError(ex,
                            "S11-CATCH — không tạo được ví {OwnerType}:{OwnerId} và ví cũng KHÔNG tồn tại sau đó. " +
                            "Lệnh cấp quà sẽ hạ cấp thành WalletMissing, không phải 500.",
                            ownerType, ownerId);
                }
            }

            // DB25b — bọc IExecutionStrategy vì Npgsql bật EnableRetryOnFailure: chiến lược retry
            // TỪ CHỐI transaction do người dùng tự mở, và khi chạy lại delegate nó KHÔNG reset change
            // tracker (chi tiết + hệ quả với sổ cái: xem <see cref="DbRetry"/>).
            return await DbRetry.RunAsync(_db, async Task<GrantResult> () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // Cộng ATOMIC (không đọc-rồi-ghi) — hai lệnh cấp song song cùng ví không đè lên nhau.
                // KHÔNG lọc theo Status: ví bị Đình chỉ vẫn nhận được quà. PAY-12 chặn HÀNH ĐỘNG tương lai
                // (reserve), còn cộng tiền vào ví là chiều ngược lại — chặn nó chỉ khiến admin không đền bù
                // được cho chính tài khoản đang có tranh chấp, tức đúng lúc cần nhất.
                var rows = await _db.CreditAccounts
                    .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.RemainingCredits, a => a.RemainingCredits + credits)
                        .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);

                if (rows == 0)
                {
                    await tx.RollbackAsync(ct);
                    return new GrantResult(GrantOutcome.WalletMissing, ownerType, ownerId, 0, 0, null);
                }

                var transaction = new CreditTransaction
                {
                    Id = Guid.NewGuid(),
                    OwnerType = ownerType,
                    OwnerId = ownerId,
                    OrderId = null,    // quà không phát sinh từ đơn nào — chính vì thế mới cần granted_by
                    SessionId = null,
                    Delta = credits,
                    Reason = CreditTransactionReason.PromoGrant,
                    GrantedBy = adminUserId,
                    Note = note,
                    GrantIdempotencyKey = idempotencyKey,
                    CreatedAt = DateTime.UtcNow
                };
                _db.CreditTransactions.Add(transaction);

                // Snapshot nằm trên ledger row để retry trả đúng response đầu, kể cả sau đó ví đã đổi số dư.
                transaction.GrantRemainingCreditsAfter = await _db.CreditAccounts.AsNoTracking()
                    .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
                    .Select(a => (int?)a.RemainingCredits)
                    .SingleAsync(ct);

                try
                {
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                // S11-CATCH — bộ lọc CŨ có thêm `IsUniqueViolation(ex)`, chỉ nhận
                // `PostgresException{SqlState:23505}` ⇒ trên SQLite luôn false ⇒ CẢ NHÁNH NÀY chưa từng
                // được một test nào chạy qua (0% coverage cho đúng đường idempotency của tiền). Bỏ bộ lọc
                // đi vẫn AN TOÀN vì quyết định thật sự nằm ở HẬU KIỂM ngay dưới: tìm thấy bút toán gốc ⇒
                // đúng là request trùng ⇒ phát lại kết quả cũ; không thấy ⇒ `throw;` y như trước.
                // Giữ vế `idempotencyKey` vì không có khoá thì không thể có đụng độ khoá, và cũng không
                // tra lại được bút toán gốc.
                catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    await tx.RollbackAsync(ct);
                    _db.ChangeTracker.Clear();

                    var original = await FindOriginalGrantAsync(ownerType, ownerId, idempotencyKey, ct);
                    if (original is not null)
                        return Replay(original);

                    throw;
                }

                _logger?.LogInformation(
                    "F20 — admin {AdminId} cấp {Credits} credit khuyến mãi cho ví {OwnerType}:{OwnerId}. Lý do: {Note}",
                    adminUserId, credits, ownerType, ownerId, note ?? "(không ghi)");

                return Replay(transaction);
            });
        }

        private async Task<CreditTransaction?> FindOriginalGrantAsync(
            OwnerType ownerType, Guid ownerId, string idempotencyKey, CancellationToken ct) =>
            await _db.CreditTransactions.AsNoTracking().FirstOrDefaultAsync(t =>
                t.OwnerType == ownerType && t.OwnerId == ownerId &&
                t.GrantIdempotencyKey == idempotencyKey &&
                t.Reason == CreditTransactionReason.PromoGrant, ct);

        private static GrantResult Replay(CreditTransaction transaction) =>
            new(GrantOutcome.Granted, transaction.OwnerType, transaction.OwnerId, transaction.Delta,
                transaction.GrantRemainingCreditsAfter
                    ?? throw new InvalidOperationException("Idempotent promo grant thiếu snapshot số dư."),
                transaction.Id);

        public async Task<SetPaymentModeResult> SetPaymentModeAsync(
            OwnerType ownerType, Guid ownerId, PaymentMode paymentMode, int? creditLimit,
            string note, bool allowStrandedCredits, Guid adminUserId,
            CancellationToken ct = default)
        {
            // payment.md D15 — "User LUÔN Prepaid". Không có khái niệm postpaid cho ví cá nhân B2C.
            if (ownerType == OwnerType.User)
                return new SetPaymentModeResult(
                    SetPaymentModeOutcome.NotOrg, ownerType, ownerId, paymentMode, creditLimit, 0, 0);

            // Postpaid PHẢI có creditLimit (>0 đã ép ở DTO [Range]); Prepaid thì KHÔNG được có creditLimit
            // (limit là khái niệm riêng của postpaid — pha trộn sẽ gây hiểu lầm "prepaid cũng có hạn mức").
            var invalidLimit =
                (paymentMode == PaymentMode.Postpaid && creditLimit is null or <= 0) ||
                (paymentMode == PaymentMode.Prepaid && creditLimit is not null);
            if (invalidLimit)
                return new SetPaymentModeResult(
                    SetPaymentModeOutcome.InvalidCreditLimit, ownerType, ownerId, paymentMode, creditLimit, 0, 0);

            // Snapshot ví — dùng làm CAS token (WHERE PaymentMode == acc.PaymentMode ở dưới) VÀ để đọc
            // remaining/reserved/period_usage cho các guard nghiệp vụ. KHÔNG tạo ví lazy: duyệt mode cho
            // một chủ ví chưa từng có ví là vô nghĩa (chưa ai mua/luyện gì để mà "duyệt trả sau").
            var acc = await _db.CreditAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId, ct);
            if (acc is null)
                return new SetPaymentModeResult(
                    SetPaymentModeOutcome.WalletMissing, ownerType, ownerId, paymentMode, creditLimit, 0, 0);

            var upgrading = acc.PaymentMode == PaymentMode.Prepaid && paymentMode == PaymentMode.Postpaid;
            var downgrading = acc.PaymentMode == PaymentMode.Postpaid && paymentMode == PaymentMode.Prepaid;

            // Prepaid→Postpaid: ví Postpaid KHÔNG dùng `remaining_credits` (nhánh reserve postpaid chỉ xét
            // period_usage/reserved/credit_limit — xem ReserveAsync). Credit đã mua còn tồn trong
            // remaining/reserved sẽ bị "mắc kẹt" (không tiêu được, không tự hoàn) nếu chuyển mà không cảnh
            // báo. CỐ Ý KHÔNG zero remaining_credits kể cả khi opt-in (BK24 plan §Cố ý không làm) — mất mát
            // không hoàn tác, chỉ cảnh báo + để admin tự quyết định qua opt-in tường minh.
            if (upgrading && (acc.RemainingCredits > 0 || acc.ReservedCredits > 0) && !allowStrandedCredits)
                return new SetPaymentModeResult(
                    SetPaymentModeOutcome.StrandedCredits, ownerType, ownerId, paymentMode, creditLimit,
                    acc.RemainingCredits, acc.ReservedCredits);

            // Postpaid→Prepaid: còn nợ (hóa đơn Issued/Overdue) hoặc kỳ hiện tại đã phát sinh sử dụng chưa
            // chốt (period_usage > 0) → chặn hạ mode. Hạ mode khi còn nợ sẽ làm mất luôn cơ chế đòi nợ
            // (guard BK17 chỉ áp cho ví Postpaid).
            if (downgrading)
            {
                var hasUnpaidInvoice = await _db.Invoices.AsNoTracking().AnyAsync(i =>
                    i.OwnerType == ownerType && i.OwnerId == ownerId &&
                    (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.Overdue), ct);

                if (hasUnpaidInvoice || (acc.PeriodUsage ?? 0) > 0)
                    return new SetPaymentModeResult(
                        SetPaymentModeOutcome.UnpaidDebt, ownerType, ownerId, paymentMode, creditLimit,
                        acc.RemainingCredits, acc.ReservedCredits);
            }

            // CAS: WHERE PaymentMode == acc.PaymentMode (snapshot) — 0 row nghĩa là ai đó đã đổi mode xen
            // giữa lúc đọc snapshot ở trên và lúc ghi ở đây (admin khác duyệt cùng lúc). KHÔNG dùng entity
            // tracked (đánh thức xmin — xem comment đầu method).
            // Downgrade về Prepaid: reset CreditLimit/PeriodUsage về NULL (không còn ý nghĩa ở Prepaid —
            // BK24 verify e2e bước 9: "limit/usage về NULL"). Upgrade lên Postpaid: PeriodUsage bắt đầu
            // sạch = 0 cho kỳ mới.
            var newPeriodUsage = paymentMode == PaymentMode.Postpaid ? (int?)0 : null;
            var newCreditLimit = paymentMode == PaymentMode.Postpaid ? creditLimit : null;

            var rows = await _db.CreditAccounts
                .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId
                            && a.PaymentMode == acc.PaymentMode)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.PaymentMode, paymentMode)
                    .SetProperty(a => a.CreditLimit, newCreditLimit)
                    .SetProperty(a => a.PeriodUsage, newPeriodUsage)
                    .SetProperty(a => a.PaymentModeChangedAt, _ => DateTime.UtcNow)
                    .SetProperty(a => a.PaymentModeChangedBy, adminUserId)
                    .SetProperty(a => a.PaymentModeChangedNote, note)
                    .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);

            if (rows == 0)
                return new SetPaymentModeResult(
                    SetPaymentModeOutcome.Conflict, ownerType, ownerId, paymentMode, creditLimit,
                    acc.RemainingCredits, acc.ReservedCredits);

            _logger?.LogWarning(
                "F23/BK24 — admin {AdminId} đổi payment mode ví {OwnerType}:{OwnerId}: {Old} → {New} " +
                "(creditLimit={CreditLimit}). Lý do: {Note}",
                adminUserId, ownerType, ownerId, acc.PaymentMode, paymentMode, creditLimit, note);

            // Đọc lại fresh cho response — remaining/reserved không đổi ở thao tác này nhưng đọc lại vẫn
            // rẻ và tránh trả số liệu snapshot cũ nếu có race vô hại khác xen vào.
            var fresh = await _db.CreditAccounts.AsNoTracking()
                .FirstAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId, ct);

            return new SetPaymentModeResult(
                SetPaymentModeOutcome.Updated, ownerType, ownerId, fresh.PaymentMode, fresh.CreditLimit,
                fresh.RemainingCredits, fresh.ReservedCredits);
        }

    }
}
