using Microsoft.EntityFrameworkCore;
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

        public WebhookService(PaymentDbContext db, ICreditAccountService accounts)
        {
            _db = db;
            _accounts = accounts;
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
                    .SetProperty(o => o.PaidAt, _ => DateTime.UtcNow), ct);

            if (moved == 0)
            {
                await tx.RollbackAsync(ct);
                return WebhookApplyOutcome.AlreadyProcessed;
            }

            var credits = order.Package?.InterviewCredits ?? 0;

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
