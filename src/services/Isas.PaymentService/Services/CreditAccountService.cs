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

            // Đọc account chỉ để CHỌN nhánh bút toán (prepaid trừ remaining P4 · postpaid dồn nợ tới
            // credit_limit P8a). Guard đầy đủ vẫn nằm trong WHERE của ExecuteUpdate (atomic self-consistent,
            // gồm cả payment_mode) → đọc rời ở đây KHÔNG phá tính chống double-spend. acc=null (không có ví)
            // rơi vào nhánh prepaid → 0 row → Insufficient (giữ hành vi no-wallet→402 của P4).
            var acc = await _db.CreditAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId, ct);

            int rows;
            if (acc?.PaymentMode == PaymentMode.Postpaid)
            {
                // POSTPAID (payment.md §Kế toán): KHÔNG trừ remaining (postpaid remaining=0), chỉ reserved+1;
                // guard ATOMIC period_usage + reserved + 1 ≤ credit_limit → 0 row = chạm hạn mức ⇒ 402
                // (PAY-5, no orphan). period_usage CHỈ tăng khi Consume (P5/P8b) — reserve KHÔNG dồn nợ kỳ
                // (bỏ ngang/release → không tính nợ). credit_limit chưa đặt (NULL) ⇒ so sánh NULL loại row ⇒
                // 402 (postpaid cần PlatformAdmin đặt hạn mức mới reserve được). Overdue-invoice block = P8b.
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
            else
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
        }

        private Task<int> ReservedCreditsOf(OwnerType ownerType, Guid ownerId, CancellationToken ct) =>
            _db.CreditAccounts.AsNoTracking()
                .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
                .Select(a => a.ReservedCredits)
                .FirstOrDefaultAsync(ct);

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

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Transition ATOMIC có guard WHERE status=Reserved: 2 consume song song cùng session → chỉ 1
            // thắng (1 row) → chỉ 1 bút toán (idempotent, chống double-process). 0 row = ai đó vừa
            // consume/release trước → hấp thụ, no-op.
            var moved = await _db.CreditReservations
                .Where(r => r.SessionId == sessionId && r.Status == ReservationStatus.Reserved)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, ReservationStatus.Consumed), ct);

            if (moved == 0)
            {
                await tx.RollbackAsync(ct);
                var raced = await _db.CreditReservations.AsNoTracking()
                    .FirstAsync(r => r.SessionId == sessionId, ct);
                return ConsumeResult.AlreadyFinalized(raced.Id);
            }

            // reserved−1 (nhả chỗ giữ). remaining KHÔNG đổi → credit "tiêu" thật thể hiện ở ledger −1.
            // (bất biến audit prepaid: remaining + reserved = Σ credit_transactions.delta vẫn giữ.)
            await _db.CreditAccounts
                .Where(a => a.OwnerType == reservation.OwnerType && a.OwnerId == reservation.OwnerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.ReservedCredits, a => a.ReservedCredits - 1)
                    .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);

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
        }

        // P6 — Release chỗ giữ khi SessionAbandoned/lỗi (D7 · payment.md §State machine "credit_reservations" +
        // "kế toán remaining↔reserved"). Reservation Reserved→Released + hoàn chỗ giữ (reserved−1, remaining+1);
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

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Transition ATOMIC có guard WHERE status=Reserved: consume & release song song cùng session →
            // chỉ 1 thắng (1 row) → chỉ 1 bên chuyển tiếp (chống double-process race consume↔release).
            // 0 row = ai đó vừa consume/release trước → hấp thụ, no-op (KHÔNG hoàn oan).
            var moved = await _db.CreditReservations
                .Where(r => r.SessionId == sessionId && r.Status == ReservationStatus.Reserved)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, ReservationStatus.Released), ct);

            if (moved == 0)
            {
                await tx.RollbackAsync(ct);
                var raced = await _db.CreditReservations.AsNoTracking()
                    .FirstAsync(r => r.SessionId == sessionId, ct);
                return ReleaseResult.AlreadyFinalized(raced.Id);
            }

            // Hoàn chỗ giữ (prepaid): reserved−1, remaining+1 (nghịch đảo của reserve) → tổng
            // remaining+reserved bảo toàn ⇒ KHÔNG ghi ledger (payment.md §Kế toán). Postpaid (period_usage)
            // để P8a — hiện P4 reserve prepaid-only nên reservation Reserved không tồn tại trên ví postpaid.
            await _db.CreditAccounts
                .Where(a => a.OwnerType == reservation.OwnerType && a.OwnerId == reservation.OwnerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.ReservedCredits, a => a.ReservedCredits - 1)
                    .SetProperty(a => a.RemainingCredits, a => a.RemainingCredits + 1)
                    .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);

            await tx.CommitAsync(ct);

            return ReleaseResult.Released(reservation.Id);
        }
    }
}
