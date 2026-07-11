using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    public class CreditAccountService : ICreditAccountService
    {
        private readonly PaymentDbContext _db;

        public CreditAccountService(PaymentDbContext db)
        {
            _db = db;
        }

        public async Task<CreditAccount> CreateAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            var exists = await _db.CreditAccounts
                .AnyAsync(x => x.OwnerType == ownerType && x.OwnerId == ownerId, ct);

            if (exists)
                throw new InvalidOperationException($"Credit account already exists for {ownerType}:{ownerId}.");

            var account = new CreditAccount
            {
                Id = Guid.NewGuid(),
                OwnerType = ownerType,
                OwnerId = ownerId,
                PaymentMode = PaymentMode.Prepaid,
                Status = CreditAccountStatus.Active,
                RemainingCredits = 0,
                ReservedCredits = 0,
                UpdatedAt = DateTime.UtcNow
            };

            _db.CreditAccounts.Add(account);
            await _db.SaveChangesAsync(ct);

            return account;
        }

        public async Task<CreditAccount?> GetAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            return await _db.CreditAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OwnerType == ownerType && x.OwnerId == ownerId, ct);
        }

        // P4 — Reserve 1 credit (D7 · payment.md §State machine "kế toán remaining↔reserved").
        // Ràng buộc: idempotent theo session_id (PAY-4), atomic chống double-spend (PAY-5),
        // hết credit → Insufficient (402) KHÔNG để lại reservation dư.
        public async Task<ReserveResult> ReserveAsync(OwnerType ownerType, Guid ownerId, Guid sessionId, CancellationToken ct = default)
        {
            // Idempotency fast-path (PAY-4): session đã có reservation → không giữ thêm, trả về nguyên trạng.
            // UNIQUE(session_id) là "khoá" idempotency; đây chỉ là đường tắt cho lần gọi lặp tuần tự.
            var existing = await _db.CreditReservations.AsNoTracking()
                .FirstOrDefaultAsync(r => r.SessionId == sessionId, ct);
            if (existing is not null)
                return ReserveResult.AlreadyReserved(existing.Id, await ReservedCreditsOf(existing.OwnerType, existing.OwnerId, ct));

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Chèn reservation TRƯỚC khi trừ số dư: UNIQUE(session_id) chặn 2 request cùng session
            // cùng trừ credit (double-spend qua race idempotency). Chỉ request thắng insert mới trừ ví.
            var reservation = new CreditReservation
            {
                Id = Guid.NewGuid(),
                OwnerType = ownerType,
                OwnerId = ownerId,
                SessionId = sessionId,
                Status = ReservationStatus.Reserved,
                CreatedAt = DateTime.UtcNow
            };
            _db.CreditReservations.Add(reservation);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) // đụng UNIQUE(session_id) → request khác đã reserve session này
            {
                _db.Entry(reservation).State = EntityState.Detached;
                await tx.RollbackAsync(ct);
                var raced = await _db.CreditReservations.AsNoTracking()
                    .FirstAsync(r => r.SessionId == sessionId, ct);
                return ReserveResult.AlreadyReserved(raced.Id, await ReservedCreditsOf(raced.OwnerType, raced.OwnerId, ct));
            }

            // Bút toán ATOMIC: 1 câu UPDATE có điều kiện (không đọc-rồi-ghi rời) → 2 reserve song song
            // không cùng vượt check remaining≥1 ⇒ chống double-spend (PAY-5). 0 row = hết credit /
            // không có ví / account Suspended. Postpaid (remaining=0) cũng rơi vào đây → 402 (hạn mức = P8a).
            var rows = await _db.CreditAccounts
                .Where(a => a.OwnerType == ownerType
                            && a.OwnerId == ownerId
                            && a.Status == CreditAccountStatus.Active
                            && a.RemainingCredits >= 1)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.RemainingCredits, a => a.RemainingCredits - 1)
                    .SetProperty(a => a.ReservedCredits, a => a.ReservedCredits + 1)
                    .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);

            if (rows == 0)
            {
                await tx.RollbackAsync(ct); // gỡ luôn reservation vừa chèn → KHÔNG để lại reservation dư
                return ReserveResult.Insufficient();
            }

            await tx.CommitAsync(ct);

            var reserved = await ReservedCreditsOf(ownerType, ownerId, ct);
            return ReserveResult.Reserved(reservation.Id, reserved);
        }

        private Task<int> ReservedCreditsOf(OwnerType ownerType, Guid ownerId, CancellationToken ct) =>
            _db.CreditAccounts.AsNoTracking()
                .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
                .Select(a => a.ReservedCredits)
                .FirstOrDefaultAsync(ct);
    }
}
