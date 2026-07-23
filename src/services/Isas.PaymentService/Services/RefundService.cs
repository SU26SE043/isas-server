using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// F18 — hoàn tiền đơn mua credit: đơn <c>Paid → Refunded</c> + **bút toán đảo** gắn bút toán mua gốc
    /// + thu hồi credit khỏi ví. Xem <see cref="IRefundService"/> cho phạm vi loại đơn.
    ///
    /// <para><b>Bất biến sổ cái được giữ nguyên vẹn.</b> Với mỗi ví:
    /// <c>remaining_credits + reserved_credits = Σ credit_transactions.delta</c>. Hoàn tiền trừ
    /// <c>remaining</c> đi đúng <c>K</c> và ghi ledger <c>−K</c> trong CÙNG transaction ⇒ hai vế dịch
    /// bằng nhau. <c>reserved</c> KHÔNG bao giờ bị đụng tới: credit đang giữ thuộc về buổi thi đang
    /// diễn ra, thu hồi nó là văng người đang thi giữa chừng (PAY-12 cấm).</para>
    ///
    /// <para><b>Vì sao thu hồi phải kẹp trần, và trần đó tính thế nào.</b> Khoản mua có thể đã bị tiêu
    /// hết. Trừ thẳng <c>remaining − purchased</c> sẽ đẩy số dư xuống âm → nổ CHECK
    /// <c>ck_credit_accounts_non_negative</c> → <c>SaveChanges</c> ném → transaction rollback ⇒ đơn
    /// KHÔNG lật được sang Refunded dù admin bấm bao nhiêu lần (mọi retry fail y hệt). Đó đúng là hình
    /// dạng lỗi DB20/DB22 vừa bịt ở vòng S8, chỉ khác điểm vào. Nên trần được tính TRƯỚC, và phần vượt
    /// trần thì hỏi người thay vì để DB ném.</para>
    ///
    /// <para><b>Credit tặng không được biến thành tiền mặt.</b> Trần thu hồi loại trừ phần suất dùng thử
    /// (F7) còn chưa tiêu. Quy ước: credit tặng được tiêu TRƯỚC (nó có mặt trong ví trước mọi khoản mua —
    /// cấp ngay lúc tạo ví), nên phần quà còn lại là
    /// <c>max(0, free_credits_granted − tổng đã tiêu)</c> và trần thu hồi là
    /// <c>max(0, remaining − phần quà còn lại)</c>. Kiểm chứng: ví được tặng 3, mua 5, chưa tiêu gì
    /// (<c>remaining=8</c>) → trần 5, hoàn xong còn đúng 3 credit quà. Cũng ví đó tiêu 5 (<c>remaining=3</c>)
    /// → quà đã tiêu sạch, 3 credit còn lại là credit đã trả tiền → trần 3, không có gì để bảo vệ.
    /// Không có phép trừ này thì hoàn tiền sẽ nuốt luôn quà: khách trả tiền, tiêu hết phần trả tiền,
    /// đòi hoàn, và mất nốt suất dùng thử — tức công ty vừa trả lại tiền vừa tịch thu quà.</para>
    /// </summary>
    public class RefundService : IRefundService
    {
        private readonly PaymentDbContext _db;
        private readonly ILogger<RefundService>? _logger;

        // Logger OPTIONAL (mẫu WebhookService/CreditAccountService): test dựng service bằng ctor 1 tham số.
        public RefundService(PaymentDbContext db, ILogger<RefundService>? logger = null)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<RefundResult> RefundOrderAsync(
            Guid orderId,
            Guid adminUserId,
            string? reason,
            string? gatewayRef,
            bool allowPartialClawback,
            bool settledNow = false,
            CancellationToken ct = default)
        {
            var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is null)
                return RefundResult.Simple(RefundOutcome.OrderNotFound, orderId);

            // Idempotent: đã hoàn rồi thì trả về nguyên trạng, KHÔNG trừ ví lần hai.
            if (order.Status == OrderStatus.Refunded)
                return new RefundResult(RefundOutcome.AlreadyRefunded, orderId, order.AmountVnd,
                    0, 0, 0, null, order.RefundedAt, order.RefundSettledAt);

            if (order.Status != OrderStatus.Paid)
                return RefundResult.Simple(RefundOutcome.NotPaid, orderId);

            if (order.Kind != OrderKind.CreditPack)
                return RefundResult.Simple(RefundOutcome.UnsupportedKind, orderId);

            // Bút toán mua gốc — nguồn chân lý cho "đơn này đã cộng bao nhiêu credit". CỐ Ý đọc từ sổ cái
            // chứ KHÔNG từ `package.interview_credits`: gói là dữ liệu SỐNG, admin sửa được sau khi bán,
            // nên đọc gói sẽ đảo nhầm số lượng của ngày hôm nay chứ không phải của lúc bán.
            var purchase = await _db.CreditTransactions.AsNoTracking()
                .Where(t => t.OrderId == orderId && t.Reason == CreditTransactionReason.Purchase)
                .OrderBy(t => t.CreatedAt)
                .FirstOrDefaultAsync(ct);

            // Đơn Paid mà không có bút toán mua: đường DB20 (gói không sinh credit → giữ Paid, log, không
            // ghi sổ). Tiền vẫn đã thu thật nên vẫn cho hoàn — chỉ là không có credit nào để thu hồi.
            var purchased = purchase?.Delta ?? 0;

            var account = await _db.CreditAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.OwnerType == order.OwnerType && a.OwnerId == order.OwnerId, ct);

            var ceiling = account is null ? 0 : await ClawbackCeilingAsync(account, ct);
            var clawback = Math.Min(purchased, ceiling);

            // Chặn TRƯỚC mọi mutation: chưa đụng gì thì chưa có gì phải rollback.
            if (clawback < purchased && !allowPartialClawback)
                return new RefundResult(RefundOutcome.InsufficientCredits, orderId, order.AmountVnd,
                    purchased, clawback, ceiling, null, null);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var refundedAt = DateTime.UtcNow;
            // Chờ chuyển tiền (null) trừ khi admin khẳng định đã chuyển ngay — mặc định để không quên gửi tiền.
            DateTime? settledAt = settledNow ? refundedAt : null;

            // Lật ATOMIC Paid→Refunded (guard WHERE status=Paid): hai admin bấm hoàn cùng lúc → chỉ 1 row
            // ⇒ chỉ 1 lần trừ ví. 0 row = ai đó vừa hoàn xong trước → hấp thụ (mẫu WebhookService).
            var moved = await _db.Orders
                .Where(o => o.Id == orderId && o.Status == OrderStatus.Paid)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatus.Refunded)
                    .SetProperty(o => o.RefundedAt, _ => refundedAt)
                    .SetProperty(o => o.RefundedBy, _ => (Guid?)adminUserId)
                    .SetProperty(o => o.RefundReason, _ => reason)
                    .SetProperty(o => o.RefundGatewayRef, _ => gatewayRef)
                    .SetProperty(o => o.RefundSettledAt, _ => settledAt)
                    // DB14 — ExecuteUpdate không đi qua SaveChanges override → stamp updated_at tường minh.
                    .SetProperty(o => o.UpdatedAt, _ => refundedAt), ct);

            if (moved == 0)
            {
                await tx.RollbackAsync(ct);
                return RefundResult.Simple(RefundOutcome.AlreadyRefunded, orderId);
            }

            Guid? refundTxId = null;

            if (clawback > 0)
            {
                // Trừ ví ATOMIC kèm guard `remaining >= clawback` — KHÔNG đọc-rồi-ghi. Số dư đọc ở trên
                // chỉ để tính trần và để hỏi người; giữa lúc đó một buổi thi khác có thể vừa reserve mất
                // credit. 0 row = số dư không còn đỡ nổi khoản trừ ⇒ huỷ TOÀN BỘ (kể cả cú lật trạng thái
                // đơn) và bảo gọi lại. Không dùng CHECK làm hàng rào: để CHECK ném thì transaction chết
                // giữa chừng và đơn kẹt Paid vĩnh viễn (bài học DB22).
                var accRows = await _db.CreditAccounts
                    .Where(a => a.OwnerType == order.OwnerType && a.OwnerId == order.OwnerId
                                && a.RemainingCredits >= clawback)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.RemainingCredits, a => a.RemainingCredits - clawback)
                        .SetProperty(a => a.UpdatedAt, _ => refundedAt), ct);

                if (accRows == 0)
                {
                    await tx.RollbackAsync(ct);
                    return new RefundResult(RefundOutcome.WalletChanged, orderId, order.AmountVnd,
                        purchased, clawback, ceiling, null, null);
                }

                var refundTx = new CreditTransaction
                {
                    Id = Guid.NewGuid(),
                    OwnerType = order.OwnerType,
                    OwnerId = order.OwnerId,
                    OrderId = orderId,
                    SessionId = null,
                    Delta = -clawback,
                    Reason = CreditTransactionReason.Refund,
                    // Liên kết + khoá idempotency (UNIQUE lọc). purchase khác null bảo đảm bởi clawback>0
                    // (clawback ≤ purchased = purchase.Delta).
                    ReversesTransactionId = purchase!.Id,
                    CreatedAt = refundedAt
                };
                _db.CreditTransactions.Add(refundTx);

                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException) // đụng UNIQUE(reverses_transaction_id) → khoản mua này đã bị đảo
                {
                    _db.Entry(refundTx).State = EntityState.Detached;
                    await tx.RollbackAsync(ct);
                    return RefundResult.Simple(RefundOutcome.AlreadyRefunded, orderId);
                }

                refundTxId = refundTx.Id;
            }
            else if (purchased > 0)
            {
                // Hoàn tiền mà KHÔNG thu hồi được credit nào (ví đã tiêu sạch, hoặc phần còn lại toàn là
                // quà được bảo vệ). Cố ghi ledger −0 sẽ vi phạm CHECK ck_credit_transactions_delta_nonzero
                // → nổ đúng kiểu DB20. Chọn: đơn vẫn Refunded (tiền trả lại là sự thật) + log to để đối
                // soát tay — thà thấy được khoản lỗ còn hơn đơn kẹt Paid ẩn mất.
                _logger?.LogError(
                    "F18 — đơn {OrderId} hoàn tiền {AmountVnd}₫ nhưng KHÔNG thu hồi được credit nào " +
                    "(đã bán {Purchased}, trần thu hồi {Ceiling}) → công ty chịu phần chênh, cần đối soát tay.",
                    orderId, order.AmountVnd, purchased, ceiling);
            }

            await tx.CommitAsync(ct);

            if (clawback < purchased)
                _logger?.LogWarning(
                    "F18 — đơn {OrderId} hoàn một phần: đã bán {Purchased} credit, chỉ thu hồi được {Clawback} " +
                    "(trần {Ceiling}). Admin {AdminId} đã chấp nhận.",
                    orderId, purchased, clawback, ceiling, adminUserId);

            return new RefundResult(RefundOutcome.Refunded, orderId, order.AmountVnd,
                purchased, clawback, ceiling, refundTxId, refundedAt, settledAt);
        }

        public async Task<SettleRefundResult> SettleRefundAsync(
            Guid orderId,
            Guid adminUserId,
            string? gatewayRef,
            CancellationToken ct = default)
        {
            var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is null)
                return SettleRefundResult.Simple(SettleOutcome.OrderNotFound, orderId);

            // Chỉ xác nhận chuyển tiền cho đơn ĐÃ hoàn — chưa Refunded thì không có dòng tiền ra nào để đối soát.
            if (order.Status != OrderStatus.Refunded)
                return SettleRefundResult.Simple(SettleOutcome.NotRefunded, orderId);

            // Đã settle trước đó → idempotent: KHÔNG dời mốc cũ (mốc đầu tiên mới là lúc tiền thật sự đi).
            if (order.RefundSettledAt is not null)
                return new SettleRefundResult(SettleOutcome.AlreadySettled, orderId,
                    order.RefundedAt, order.RefundSettledAt, order.RefundGatewayRef);

            var settledAt = DateTime.UtcNow;

            // Đóng dấu ATOMIC (guard WHERE status=Refunded AND refund_settled_at IS NULL): hai admin bấm
            // xác nhận cùng lúc → chỉ 1 row set mốc; kẻ thua thấy 0 row → đọc lại → AlreadySettled (idempotent).
            // Chỉ ghi đè gatewayRef khi truyền mới — không xoá mã đã có bằng null.
            var moved = await _db.Orders
                .Where(o => o.Id == orderId && o.Status == OrderStatus.Refunded && o.RefundSettledAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.RefundSettledAt, _ => settledAt)
                    .SetProperty(o => o.RefundGatewayRef,
                        o => gatewayRef ?? o.RefundGatewayRef)
                    // DB14 — ExecuteUpdate không đi qua SaveChanges override → stamp updated_at tường minh.
                    .SetProperty(o => o.UpdatedAt, _ => settledAt), ct);

            if (moved == 0)
            {
                // Đua thua: ai đó vừa settle xong → đọc lại trạng thái hiện tại, trả AlreadySettled.
                var fresh = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
                return new SettleRefundResult(SettleOutcome.AlreadySettled, orderId,
                    fresh?.RefundedAt, fresh?.RefundSettledAt, fresh?.RefundGatewayRef);
            }

            _logger?.LogInformation(
                "F18 — đơn {OrderId} xác nhận đã chuyển tiền hoàn cho khách (admin {AdminId}, ref {Ref}).",
                orderId, adminUserId, gatewayRef ?? order.RefundGatewayRef);

            return new SettleRefundResult(SettleOutcome.Settled, orderId,
                order.RefundedAt, settledAt, gatewayRef ?? order.RefundGatewayRef);
        }

        /// <summary>
        /// Trần thu hồi = <c>max(0, remaining − quà chưa tiêu)</c>, với quà chưa tiêu =
        /// <c>max(0, free_credits_granted − tổng đã tiêu)</c> (quà tiêu trước — xem <see cref="RefundService"/>).
        /// Chỉ đọc <c>remaining</c>: <c>reserved</c> thuộc về buổi thi đang chạy (PAY-12).
        /// </summary>
        private async Task<int> ClawbackCeilingAsync(CreditAccount account, CancellationToken ct)
        {
            // Tổng đã tiêu = tổng các bút toán ÂM (Consume −1, và cả Refund trước đó). Đọc từ sổ cái vì
            // sổ cái là nơi duy nhất còn nhớ lịch sử; số dư hiện tại thì không.
            var negativeSum = await _db.CreditTransactions
                .Where(t => t.OwnerType == account.OwnerType && t.OwnerId == account.OwnerId && t.Delta < 0)
                .SumAsync(t => (int?)t.Delta, ct) ?? 0;

            var totalSpent = -negativeSum;
            var freeUnspent = Math.Max(0, account.FreeCreditsGranted - totalSpent);
            return Math.Max(0, account.RemainingCredits - freeUnspent);
        }
    }
}
