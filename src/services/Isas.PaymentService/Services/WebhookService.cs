using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P2 — cộng credit khi PayOS báo <c>Paid</c> (payment.md §Luồng mua pack + §State machine). Idempotent
    /// theo <c>payos_order_code</c> (PAY-8): chỉ đơn Pending mới được cộng, đơn terminal bất biến (PAY-10).
    /// Chủ ví lấy từ chính order (nguồn chân lý — dựng lúc CreateOrder), không tin payload PayOS.
    /// </summary>
    public class WebhookService : IWebhookService
    {
        private readonly PaymentDbContext _db;
        private readonly ICreditAccountService _accounts;
        private readonly ILogger<WebhookService>? _logger;
        private readonly ISubscriptionService? _subscriptions;

        // DB20 — logger inject OPTIONAL (mẫu AI4): ctor 2-tham-số đang được dùng ở nhiều site test;
        // thêm dependency bắt buộc chỉ để log sẽ phải sửa hết mà không đem lại giá trị nào.
        // F8 — subscription service cũng OPTIONAL (cùng lý do). Null + đơn thuê bao → đơn vẫn Paid + log
        // lỗi, KHÔNG cộng credit; cấu hình thật luôn có (Program.cs đăng ký).
        public WebhookService(PaymentDbContext db, ICreditAccountService accounts,
            ILogger<WebhookService>? logger = null,
            ISubscriptionService? subscriptions = null)
        {
            _db = db;
            _accounts = accounts;
            _logger = logger;
            _subscriptions = subscriptions;
        }

        public async Task<WebhookApplyOutcome> ApplyPaidWebhookAsync(
            long payosOrderCode, string? gatewayTxnId, string rawPayload, CancellationToken ct = default)
        {
            // Đọc đơn + gói (interview_credits). AsNoTracking: transition/cộng credit làm bằng ExecuteUpdate
            // (atomic, không đọc-rồi-ghi) nên không cần entity tracked.
            var order = await _db.Orders
                .AsNoTracking()
                .Include(o => o.Package)
                .FirstOrDefaultAsync(o => o.PayosOrderCode == payosOrderCode, ct);

            // Không khớp đơn nào (ping-test PayOS orderCode=123 / đơn service khác): lưu bằng chứng đối soát
            // (order_id null) — KHÔNG cộng credit. append-only.
            if (order is null)
            {
                _db.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = null,
                    Gateway = "payos",
                    GatewayTxnId = gatewayTxnId,
                    Status = "success",
                    RawWebhookPayload = rawPayload,
                    CreatedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(ct);
                return WebhookApplyOutcome.OrderNotFound;
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // 1) Transition ATOMIC Pending→Paid (guard WHERE status=Pending): 2 webhook redeliver cùng lúc →
            //    chỉ 1 thắng (1 row) ⇒ chỉ 1 lần cộng credit (idempotent PAY-8). 0 row = đã Paid/terminal
            //    (PAY-10 bất biến) → no-op, KHÔNG cộng credit lần 2.
            var moved = await _db.Orders
                .Where(o => o.PayosOrderCode == payosOrderCode && o.Status == OrderStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatus.Paid)
                    .SetProperty(o => o.PaidAt, _ => DateTime.UtcNow)
                    // DB14 — ExecuteUpdate bỏ qua SaveChanges override → stamp updated_at tường minh.
                    .SetProperty(o => o.UpdatedAt, _ => DateTime.UtcNow), ct);

            if (moved == 0)
            {
                await tx.RollbackAsync(ct);
                return WebhookApplyOutcome.AlreadyProcessed;
            }

            // P8b — branch theo Kind: đơn InvoiceSettlement tất toán hóa đơn postpaid, KHÔNG cộng credit.
            // Hóa đơn Issued/Overdue → Paid (guard WHERE status ∈ {Issued,Overdue} → idempotent: đã Paid/Void
            // → 0 row → no-op). Order đã guard Pending→Paid (moved==1) ở trên nên đây chạy đúng 1 lần/đơn.
            if (order.Kind == OrderKind.InvoiceSettlement)
            {
                if (order.InvoiceId is Guid invoiceId)
                {
                    await _db.Invoices
                        .Where(i => i.Id == invoiceId
                                    && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.Overdue))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(i => i.Status, InvoiceStatus.Paid), ct);
                }

                // Log sự kiện gateway (append-only) — bằng chứng đối soát, KHÔNG ghi credit_transactions.
                _db.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    Gateway = "payos",
                    GatewayTxnId = gatewayTxnId,
                    Status = "success",
                    RawWebhookPayload = rawPayload,
                    CreatedAt = DateTime.UtcNow
                });

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return WebhookApplyOutcome.InvoiceSettled;
            }

            // F8 — branch thuê bao: KHÔNG cộng credit, KHÔNG ghi credit_transactions (mẫu InvoiceSettlement
            // ngay trên). Đây là lý do gói thuê bao KHÔNG cần gỡ guard DB20: dòng `credits ?? 0` bên dưới
            // — thứ đẻ ra ledger Delta=0 → nổ CHECK → rollback flip Pending→Paid → đơn kẹt Pending vĩnh
            // viễn dù khách đã trả tiền — nằm ngoài đường đi của đơn thuê bao.
            if (order.Kind is OrderKind.SubscriptionPurchase or OrderKind.SubscriptionRenewal)
            {
                // Ví phải tồn tại trước khi ghi subscriptions (FK composite owner → credit_accounts, DB9),
                // và người mua gói tháng đằng nào cũng cần ví để reserve được (FK trên credit_reservations).
                // Cùng lối xử lý race với nhánh CreditPack bên dưới.
                if (await _accounts.GetAccountAsync(order.OwnerType, order.OwnerId, ct) is null)
                {
                    try
                    {
                        await _accounts.CreateAccountAsync(order.OwnerType, order.OwnerId, ct);
                    }
                    catch (DbUpdateException) // đối thủ vừa tạo trước — ví đã tồn tại là đủ
                    {
                        foreach (var entry in _db.ChangeTracker.Entries<CreditAccount>().ToList())
                            entry.State = EntityState.Detached;
                    }
                }

                Subscription? activated = null;
                if (_subscriptions is not null && order.Package is not null)
                    activated = await _subscriptions.ActivateAsync(
                        order.OwnerType, order.OwnerId, order.Id, order.Package, ct);

                if (activated is null)
                    _logger?.LogError(
                        "Đơn {OrderId} (payos {OrderCode}) đã Paid nhưng KHÔNG kích hoạt được kỳ hạn thuê bao " +
                        "(package {PackageId}) → cần đối soát tay.",
                        order.Id, payosOrderCode, order.PackageId);

                _db.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    Gateway = "payos",
                    GatewayTxnId = gatewayTxnId,
                    Status = "success",
                    RawWebhookPayload = rawPayload,
                    CreatedAt = DateTime.UtcNow
                });

                // Kỳ hạn + flip Pending→Paid + log gateway commit CHUNG một transaction ⇒ không có trạng
                // thái trung gian "đã Paid mà chưa có quyền dùng".
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return WebhookApplyOutcome.SubscriptionActivated;
            }

            var credits = order.Package?.InterviewCredits ?? 0;

            // DB20 — defense-in-depth: OrderService chặn không cho TẠO đơn CreditPack với gói không sinh
            // credit, nhưng đơn CŨ đã nằm sẵn trong DB (tạo trước fix) vẫn có thể rơi vào đây. Nếu để
            // credits=0 đi tiếp thì ledger Delta=0 vi phạm CHECK ck_credit_transactions_delta_nonzero →
            // SaveChanges ném → tx.Commit không chạy → flip Pending→Paid rollback theo ⇒ khách trả tiền
            // mà đơn kẹt Pending vĩnh viễn (deterministic: mọi retry đều fail y hệt).
            // Chọn GIỮ đơn ở Paid + log bằng chứng, KHÔNG cộng credit và KHÔNG ghi ledger: tiền đã vào
            // thật nên trạng thái Paid là đúng sự thật; phần credit thiếu để đối soát tay (PAY-10 terminal
            // bất biến) — thà đơn Paid thiếu credit và thấy được, còn hơn đơn Pending vĩnh viễn ẩn mất.
            if (credits <= 0)
            {
                _db.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    Gateway = "payos",
                    GatewayTxnId = gatewayTxnId,
                    Status = "success",
                    RawWebhookPayload = rawPayload,
                    CreatedAt = DateTime.UtcNow
                });

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _logger?.LogError(
                    "Đơn {OrderId} (payos {OrderCode}) đã Paid nhưng gói {PackageId} không sinh credit " +
                    "(InterviewCredits={Credits}) → KHÔNG cộng credit, cần đối soát tay.",
                    order.Id, payosOrderCode, order.PackageId, order.Package?.InterviewCredits);

                return WebhookApplyOutcome.Credited;
            }

            // 2) Đảm bảo ví tồn tại (lần mua đầu của chủ ví → chưa có account). Tạo trong CÙNG transaction
            //    (CreditAccountService dùng chung DbContext scoped). Race 2 đơn khác nhau cùng chủ ví cùng
            //    tạo account → 1 thắng, bên thua đụng UNIQUE(owner) → nuốt, account đã tồn tại là đủ.
            var account = await _accounts.GetAccountAsync(order.OwnerType, order.OwnerId, ct);
            if (account is null)
            {
                try
                {
                    await _accounts.CreateAccountAsync(order.OwnerType, order.OwnerId, ct);
                }
                catch (DbUpdateException) // đối thủ vừa tạo trước — account đã tồn tại, tiếp tục cộng
                {
                    foreach (var entry in _db.ChangeTracker.Entries<CreditAccount>().ToList())
                        entry.State = EntityState.Detached;
                }
            }

            // 3) Cộng credit ATOMIC theo chủ ví (không đọc-rồi-ghi). Prepaid pack → remaining_credits += credits.
            await _db.CreditAccounts
                .Where(a => a.OwnerType == order.OwnerType && a.OwnerId == order.OwnerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.RemainingCredits, a => a.RemainingCredits + credits)
                    .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);

            // 4) Sổ cái Purchase (+credits) — gắn order_id, session_id null.
            _db.CreditTransactions.Add(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                OwnerType = order.OwnerType,
                OwnerId = order.OwnerId,
                OrderId = order.Id,
                SessionId = null,
                Delta = credits,
                Reason = CreditTransactionReason.Purchase,
                CreatedAt = DateTime.UtcNow
            });

            // 5) Log sự kiện gateway (append-only) — bằng chứng đối soát.
            _db.PaymentTransactions.Add(new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Gateway = "payos",
                GatewayTxnId = gatewayTxnId,
                Status = "success",
                RawWebhookPayload = rawPayload,
                CreatedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return WebhookApplyOutcome.Credited;
        }
    }
}
