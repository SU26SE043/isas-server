using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

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
            OwnerType ownerType, Guid ownerId, int credits, string? note, Guid adminUserId,
            CancellationToken ct = default)
        {
            // Cấp 0 (hoặc âm) không có nghĩa, và bút toán delta = 0 vi phạm CHECK
            // ck_credit_transactions_delta_nonzero → SaveChanges ném giữa transaction. Chặn ở cửa thay vì
            // để DB ném (bài học DB20: lỗi ném từ trong tx làm hỏng cả những thứ đã làm trước đó).
            // Trừ credit thì phải đi đường hoàn tiền F18 (có bút toán đảo gắn khoản gốc), không phải
            // "cấp số âm" — nếu không thì admin có một đường trừ credit không dấu vết.
            if (credits <= 0)
                return new GrantResult(GrantOutcome.InvalidAmount, ownerType, ownerId, 0, 0, null);

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
                catch (DbUpdateException) // đối thủ vừa tạo trước — ví tồn tại là đủ (mẫu WebhookService)
                {
                    foreach (var entry in _db.ChangeTracker.Entries<CreditAccount>().ToList())
                        entry.State = EntityState.Detached;
                    foreach (var entry in _db.ChangeTracker.Entries<CreditTransaction>().ToList())
                        entry.State = EntityState.Detached;
                }
                catch (InvalidOperationException) // CreateAccountAsync tự thấy ví đã có (check-then-act)
                {
                }
            }

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
                CreatedAt = DateTime.UtcNow
            };
            _db.CreditTransactions.Add(transaction);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger?.LogInformation(
                "F20 — admin {AdminId} cấp {Credits} credit khuyến mãi cho ví {OwnerType}:{OwnerId}. Lý do: {Note}",
                adminUserId, credits, ownerType, ownerId, note ?? "(không ghi)");

            var remaining = await _db.CreditAccounts.AsNoTracking()
                .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
                .Select(a => a.RemainingCredits)
                .FirstOrDefaultAsync(ct);

            return new GrantResult(GrantOutcome.Granted, ownerType, ownerId, credits, remaining, transaction.Id);
        }
    }
}
