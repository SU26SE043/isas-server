using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayOS.Models.Webhooks;
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
        private readonly IPayoutClient? _payout;
        private readonly IBankBinResolver? _bins;
        private readonly RefundPayoutSettings _payoutOptions;

        // Logger OPTIONAL (mẫu WebhookService/CreditAccountService): test dựng service bằng ctor 1 tham số.
        // Ba tham số chi tiền cũng optional vì phần lớn test F18 chỉ quan tâm sổ sách; thiếu chúng thì
        // đường chi tự động tự tắt (NotEnabled) chứ không ném.
        public RefundService(
            PaymentDbContext db,
            ILogger<RefundService>? logger = null,
            IPayoutClient? payout = null,
            IBankBinResolver? bins = null,
            IOptions<RefundPayoutSettings>? payoutOptions = null)
        {
            _db = db;
            _logger = logger;
            _payout = payout;
            _bins = bins;
            _payoutOptions = payoutOptions?.Value ?? new RefundPayoutSettings();
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

        public async Task<RefundPayoutResult> InitiateRefundPayoutAsync(
            Guid orderId, Guid adminUserId, CancellationToken ct = default)
        {
            var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is null)
                return new RefundPayoutResult(RefundPayoutOutcome.OrderNotFound, orderId);

            if (order.Status != OrderStatus.Refunded)
                return new RefundPayoutResult(RefundPayoutOutcome.NotRefunded, orderId);

            // Đã chuyển tiền rồi → KHÔNG chuyển lần hai. Guard này đứng trước mọi thứ khác vì nó là
            // ranh giới giữa "một lần hoàn" và "hoàn hai lần".
            if (order.RefundSettledAt is not null)
                return new RefundPayoutResult(RefundPayoutOutcome.AlreadySettled, orderId,
                    order.PayoutId, order.RefundSettledAt);

            if (!_payoutOptions.Enabled || _payout is null || _bins is null || !_payout.IsConfigured)
                return new RefundPayoutResult(RefundPayoutOutcome.NotEnabled, orderId,
                    Message: "Chi tiền hoàn tự động chưa bật hoặc chưa cấu hình kênh chi payOS.");

            // Đã có lệnh đang bay → theo tiếp lệnh CŨ, tuyệt đối không mở lệnh mới.
            if (order.PayoutIdempotencyKey is not null && order.PayoutStatus == PayoutStatus.InFlight)
                return await PollRefundPayoutAsync(orderId, ct);

            // Lệnh hỏng là ĐIỂM DỪNG của đường tự động. Thử lại đòi một khoá idempotency mới, tức là
            // mở lại đúng cánh cửa chuyển-tiền-hai-lần mà cả thiết kế này dựng lên để đóng — nên việc
            // thử lại là quyết định của người, qua đường chuyển tay.
            if (order.PayoutStatus == PayoutStatus.Failed)
                return new RefundPayoutResult(RefundPayoutOutcome.Rejected, orderId, order.PayoutId,
                    Message: order.PayoutFailureReason
                             ?? "Lệnh chi trước đó đã hỏng — chuyển tay và xác nhận bằng /refund/settle.");

            if (_payoutOptions.MaxAutoPayoutVnd <= 0 || order.AmountVnd > _payoutOptions.MaxAutoPayoutVnd)
                return new RefundPayoutResult(RefundPayoutOutcome.OverCeiling, orderId,
                    Message: $"Số tiền {order.AmountVnd:N0}₫ vượt trần chi tự động "
                             + $"{_payoutOptions.MaxAutoPayoutVnd:N0}₫ — chuyển tay.");

            var payer = await ReadPayerAsync(orderId, ct);
            var bin = _bins.Resolve(payer.BankId);
            if (bin is null || string.IsNullOrWhiteSpace(payer.AccountNumber))
                return new RefundPayoutResult(RefundPayoutOutcome.DestinationUnresolved, orderId,
                    Message: $"Không dựng được đích chuyển từ webhook gốc (mã ngân hàng '{payer.BankId}', "
                             + "số tài khoản " + (string.IsNullOrWhiteSpace(payer.AccountNumber) ? "trống" : "có")
                             + ") — chuyển tay.");

            // Số dư đọc được mà không đủ → dừng sớm. Đọc KHÔNG được (null) thì không chặn: "không biết"
            // không phải "bằng 0", và payOS vẫn từ chối được ở bước sau nếu thật sự thiếu tiền.
            var balance = await _payout.GetBalanceAsync(ct);
            if (balance is not null && balance < order.AmountVnd)
                return new RefundPayoutResult(RefundPayoutOutcome.InsufficientBalance, orderId,
                    Message: $"Ví chi còn {balance:N0}₫, cần {order.AmountVnd:N0}₫.");

            // ── GHI KHOÁ IDEMPOTENCY TRƯỚC KHI GỌI ─────────────────────────────────────────────────
            // Ghi trước, và ghi bằng một câu UPDATE có điều kiện `payout_idempotency_key IS NULL`. Hai
            // tính chất, hai mục đích khác nhau:
            //   • ghi TRƯỚC ⇒ nếu tiến trình chết ngay sau lời gọi mạng, khoá vẫn còn trên đĩa và lần
            //     hỏi lại dùng đúng khoá đó ⇒ payOS nhận ra lệnh trùng thay vì chuyển tiền lần hai;
            //   • điều kiện IS NULL ⇒ hai admin bấm cùng lúc thì chỉ một người giành được khoá.
            var key = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var claimed = await _db.Orders
                .Where(o => o.Id == orderId
                            && o.Status == OrderStatus.Refunded
                            && o.RefundSettledAt == null
                            && o.PayoutIdempotencyKey == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.PayoutIdempotencyKey, _ => (Guid?)key)
                    .SetProperty(o => o.PayoutStatus, _ => (PayoutStatus?)PayoutStatus.InFlight)
                    .SetProperty(o => o.UpdatedAt, _ => now), ct);

            if (claimed == 0)
                // Người khác vừa giành khoá xong → theo lệnh của họ, không mở lệnh song song.
                return await PollRefundPayoutAsync(orderId, ct);

            _logger?.LogInformation(
                "Chi tiền hoàn đơn {OrderId}: {Amount}₫ → {Bin}/{Account} (admin {AdminId}).",
                orderId, order.AmountVnd, bin, Mask(payer.AccountNumber), adminUserId);

            var created = await _payout.CreateAsync(
                orderId.ToString(), order.AmountVnd, BuildDescription(orderId),
                bin, payer.AccountNumber!, key, ct);

            return await ApplyPayoutOutcomeAsync(orderId, adminUserId, created.Outcome,
                created.Payout, created.Message, payer.AccountName, ct);
        }

        public async Task<RefundPayoutResult> PollRefundPayoutAsync(Guid orderId, CancellationToken ct = default)
        {
            var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is null)
                return new RefundPayoutResult(RefundPayoutOutcome.OrderNotFound, orderId);

            if (order.RefundSettledAt is not null)
                return new RefundPayoutResult(RefundPayoutOutcome.AlreadySettled, orderId,
                    order.PayoutId, order.RefundSettledAt);

            if (_payout is null || !_payout.IsConfigured || !_payoutOptions.Enabled)
                return new RefundPayoutResult(RefundPayoutOutcome.NotEnabled, orderId);

            if (order.PayoutStatus != PayoutStatus.InFlight || order.PayoutIdempotencyKey is null)
                return new RefundPayoutResult(RefundPayoutOutcome.NotEnabled, orderId,
                    Message: "Đơn không có lệnh chi nào đang bay.");

            var payer = await ReadPayerAsync(orderId, ct);

            // Chưa có payoutId = lời gọi tạo lệnh không trả về được (timeout). Đường duy nhất để biết
            // lệnh có tồn tại hay không là GỌI LẠI BẰNG ĐÚNG KHOÁ CŨ: payOS hoặc trả lại lệnh cũ, hoặc
            // báo trùng khoá — cả hai đều KHÔNG sinh thêm lệnh. (API danh sách lệnh chi chỉ lọc được
            // limit/offset, không tra được theo referenceId, nên không dùng đường đó.)
            if (string.IsNullOrEmpty(order.PayoutId))
            {
                var bin = _bins?.Resolve(payer.BankId);
                if (bin is null || string.IsNullOrWhiteSpace(payer.AccountNumber))
                    return new RefundPayoutResult(RefundPayoutOutcome.DestinationUnresolved, orderId);

                var retried = await _payout.CreateAsync(
                    orderId.ToString(), order.AmountVnd, BuildDescription(orderId),
                    bin, payer.AccountNumber!, order.PayoutIdempotencyKey.Value, ct);

                return await ApplyPayoutOutcomeAsync(orderId, Guid.Empty, retried.Outcome,
                    retried.Payout, retried.Message, payer.AccountName, ct);
            }

            var snapshot = await _payout.GetAsync(order.PayoutId, ct);
            if (snapshot is null)
                // Không tra được ≠ hỏng. Giữ nguyên đang-bay, hỏi lại vòng sau.
                return new RefundPayoutResult(RefundPayoutOutcome.InFlight, orderId, order.PayoutId);

            return await ApplyPayoutOutcomeAsync(orderId, Guid.Empty, PayoutCallOutcome.Created,
                snapshot, null, payer.AccountName, ct);
        }

        /// <summary>
        /// Quy một kết quả từ payOS về hành động trên đơn. Tách riêng vì cả đường bấm nút lẫn đường
        /// reconciler đều phải xử lý y hệt nhau — hai bản sao của luật tiền là hai bản sao để lệch nhau.
        /// </summary>
        private async Task<RefundPayoutResult> ApplyPayoutOutcomeAsync(
            Guid orderId,
            Guid actorId,
            PayoutCallOutcome outcome,
            PayoutSnapshot? snapshot,
            string? message,
            string? payerName,
            CancellationToken ct)
        {
            switch (outcome)
            {
                case PayoutCallOutcome.Rejected:
                    await MarkPayoutFailedAsync(orderId, message, ct);
                    return new RefundPayoutResult(RefundPayoutOutcome.Rejected, orderId, Message: message);

                // Không biết kết quả, hoặc biết chắc lệnh đã tồn tại nhưng không lấy được id: cả hai đều
                // là "giữ nguyên đang bay". Khoá idempotency đã nằm trên đĩa nên vòng sau hỏi lại an toàn.
                case PayoutCallOutcome.Unknown:
                case PayoutCallOutcome.AlreadyExists when snapshot is null:
                    return new RefundPayoutResult(RefundPayoutOutcome.InFlight, orderId, Message: message);
            }

            if (snapshot is null)
                return new RefundPayoutResult(RefundPayoutOutcome.InFlight, orderId, Message: message);

            var now = DateTime.UtcNow;

            if (snapshot.State == PayoutState.Failed)
            {
                await MarkPayoutFailedAsync(orderId, snapshot.Message ?? message, ct);
                return new RefundPayoutResult(RefundPayoutOutcome.Rejected, orderId, snapshot.PayoutId,
                    Message: snapshot.Message);
            }

            if (snapshot.State != PayoutState.Succeeded)
            {
                // Mới nhận / đang xử lý → chỉ lưu lại id để vòng sau tra được, KHÔNG đóng dấu gì.
                await _db.Orders
                    .Where(o => o.Id == orderId && o.PayoutId == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.PayoutId, _ => snapshot.PayoutId)
                        .SetProperty(o => o.UpdatedAt, _ => now), ct);

                return new RefundPayoutResult(RefundPayoutOutcome.InFlight, orderId, snapshot.PayoutId);
            }

            // ── payOS báo ĐÃ CHUYỂN XONG ────────────────────────────────────────────────────────────
            // Đối chiếu tên chủ tài khoản nhận với tên người đã trả tiền. Đây là lưới bắt ca hiểm nhất
            // còn lại: mã ngân hàng đích suy sai ⇒ CÙNG số tài khoản ở ngân hàng KHÁC vẫn tồn tại và
            // ValidateDestination vẫn cho qua ⇒ tiền tới một người có thật, nhưng không phải khách.
            var matched = NamesMatch(payerName, snapshot.ToAccountName);

            if (matched == false)
            {
                _logger?.LogError(
                    "Đơn {OrderId}: lệnh chi {PayoutId} ĐÃ chuyển xong nhưng tên người nhận '{Received}' " +
                    "KHÔNG khớp người đã trả '{Expected}'. KHÔNG đóng dấu đã hoàn — cần đối soát NGAY.",
                    orderId, snapshot.PayoutId, snapshot.ToAccountName, payerName);

                await _db.Orders
                    .Where(o => o.Id == orderId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.PayoutId, _ => snapshot.PayoutId)
                        .SetProperty(o => o.PayoutStatus, _ => (PayoutStatus?)PayoutStatus.Succeeded)
                        .SetProperty(o => o.PayoutFailureReason, _ =>
                            $"Tiền đã chuyển nhưng tên người nhận '{snapshot.ToAccountName}' không khớp "
                            + $"người đã trả '{payerName}' — cần đối soát.")
                        .SetProperty(o => o.UpdatedAt, _ => now), ct);

                return new RefundPayoutResult(RefundPayoutOutcome.NameMismatch, orderId, snapshot.PayoutId,
                    Message: "Tiền đã chuyển nhưng tên người nhận không khớp — chưa đóng dấu đã hoàn.");
            }

            if (matched is null)
                // Webhook gốc không có tên (đo thật: 3/15 giao dịch) ⇒ mất bộ dò, nhưng KHÔNG có dấu hiệu
                // sai nào. Vẫn đóng dấu (tiền đã đi là sự thật) và ghi log để còn lần theo được.
                _logger?.LogWarning(
                    "Đơn {OrderId}: không đối chiếu được tên người nhận (webhook gốc không có tên). " +
                    "Vẫn đóng dấu đã hoàn theo xác nhận của payOS.", orderId);

            await _db.Orders
                .Where(o => o.Id == orderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.PayoutId, _ => snapshot.PayoutId)
                    .SetProperty(o => o.PayoutStatus, _ => (PayoutStatus?)PayoutStatus.Succeeded)
                    .SetProperty(o => o.UpdatedAt, _ => now), ct);

            // Tái dùng đường settle sẵn có (idempotent, guard `refund_settled_at IS NULL`) thay vì tự
            // đóng dấu — một chỗ duy nhất định nghĩa "tiền đã đi" cho cả đường tay lẫn đường tự động.
            var settled = await SettleRefundAsync(orderId, actorId, snapshot.PayoutId, ct);

            return new RefundPayoutResult(RefundPayoutOutcome.Settled, orderId, snapshot.PayoutId,
                settled.RefundSettledAt);
        }

        private async Task MarkPayoutFailedAsync(Guid orderId, string? reason, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            await _db.Orders
                .Where(o => o.Id == orderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.PayoutStatus, _ => (PayoutStatus?)PayoutStatus.Failed)
                    .SetProperty(o => o.PayoutFailureReason, _ => reason)
                    .SetProperty(o => o.UpdatedAt, _ => now), ct);
        }

        /// <summary>Mô tả lệnh chi. payOS giới hạn độ dài mô tả nên cắt ngắn có chủ đích (PAY-9).</summary>
        private static string BuildDescription(Guid orderId) =>
            $"Hoan tien {orderId.ToString()[..8]}";

        /// <summary>
        /// So tên chủ tài khoản. <c>null</c> = không đủ dữ liệu để so (một trong hai vế trống) — CỐ Ý
        /// khác <c>false</c>: "không biết" không được phép biến thành cáo buộc chuyển nhầm.
        ///
        /// So sau khi bỏ dấu + bỏ mọi ký tự không phải chữ/số, vì tên do ngân hàng trả về thường viết hoa
        /// không dấu ("NGUYEN VAN A") còn tên trong webhook có thể khác cách đặt khoảng trắng.
        /// </summary>
        public static bool? NamesMatch(string? expected, string? received)
        {
            var a = NormalizeName(expected);
            var b = NormalizeName(received);
            if (a.Length == 0 || b.Length == 0) return null;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            var decomposed = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }

        private static string Mask(string? accountNumber) =>
            string.IsNullOrEmpty(accountNumber) || accountNumber.Length <= 4
                ? "****"
                : $"****{accountNumber[^4..]}";

        /// <summary>
        /// Đọc tài khoản người đã trả tiền từ webhook gốc đã lưu (<c>payment_transactions</c>).
        ///
        /// <para>CỐ Ý không sao chép số tài khoản sang cột riêng trên <c>orders</c>: đó là dữ liệu cá
        /// nhân, bản gốc đã nằm sẵn trong log append-only, và nhân bản nó ra bảng thứ hai chỉ tạo thêm
        /// một chỗ phải bảo vệ và phải dọn.</para>
        /// </summary>
        private async Task<PayerAccount> ReadPayerAsync(Guid orderId, CancellationToken ct)
        {
            var raws = await _db.PaymentTransactions.AsNoTracking()
                .Where(t => t.OrderId == orderId && t.RawWebhookPayload != null)
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => t.RawWebhookPayload!)
                .Take(10)
                .ToListAsync(ct);

            foreach (var raw in raws)
            {
                WebhookData? data;
                try
                {
                    data = JsonSerializer.Deserialize<Webhook>(raw)?.Data;
                }
                catch (JsonException)
                {
                    continue;   // bản ghi hỏng không được làm chết cả lệnh hoàn
                }

                if (data is null || string.IsNullOrWhiteSpace(data.CounterAccountNumber)) continue;

                return new PayerAccount(
                    data.CounterAccountBankId, data.CounterAccountNumber, data.CounterAccountName);
            }

            return new PayerAccount(null, null, null);
        }

        private sealed record PayerAccount(string? BankId, string? AccountNumber, string? AccountName);

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
