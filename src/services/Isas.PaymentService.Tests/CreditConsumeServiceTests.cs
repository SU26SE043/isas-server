using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// P5 — POST /internal/credits/consume (khi SessionScored). Verify:
// (a) consume → reservation Consumed + reserved−1 + đúng 1 credit_transactions(Consume, −1); remaining giữ nguyên.
// (b) gọi 2 lần cùng sessionId → vẫn 1 transaction (idempotent/absorbing PAY-11).
// (c) reservation lạ (chưa có) / đã Released → no-op an toàn, KHÔNG trừ oan.
public class CreditConsumeServiceTests
{
    private static async Task<CreditAccount> SeedAccountAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining, int reserved = 0,
        CreditAccountStatus status = CreditAccountStatus.Active)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = status,
            RemainingCredits = remaining,
            ReservedCredits = reserved,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(acc);
        await tdb.Db.SaveChangesAsync();
        return acc;
    }

    private static async Task<CreditAccount> SeedPostpaidAccountAsync(
        PaymentTestDb tdb, Guid orgId, int? creditLimit, int periodUsage = 0, int reserved = 0,
        CreditAccountStatus status = CreditAccountStatus.Active)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = PaymentMode.Postpaid,
            Status = status,
            RemainingCredits = 0,          // postpaid KHÔNG dùng remaining
            ReservedCredits = reserved,
            CreditLimit = creditLimit,
            PeriodUsage = periodUsage,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(acc);
        await tdb.Db.SaveChangesAsync();
        return acc;
    }

    private static async Task SeedReservationAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, Guid sessionId, ReservationStatus status)
    {
        tdb.Db.CreditReservations.Add(new CreditReservation
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            SessionId = sessionId,
            Status = status,
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
    }

    // (a) session đã reserve → consume: reservation Consumed, reserved−1 (remaining giữ nguyên),
    //     đúng 1 credit_transactions(Consume, −1).
    [Fact]
    public async Task Consume_SessionDaReserve_TruThat_GhiLedger()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);

        // reserve trước (state thật): remaining 5→4, reserved 0→1.
        await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);

        var result = await new CreditAccountService(tdb.NewContext())
            .ConsumeAsync(sessionId);

        Assert.Equal(ConsumeOutcome.Consumed, result.Outcome);
        Assert.NotNull(result.ReservationId);

        using var read = tdb.NewContext();
        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Consumed, reservation.Status);

        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(4, acc.RemainingCredits);   // remaining KHÔNG đổi khi consume
        Assert.Equal(0, acc.ReservedCredits);    // reserved −1

        var ledger = await read.CreditTransactions.Where(t => t.SessionId == sessionId).ToListAsync();
        Assert.Single(ledger);
        Assert.Equal(-1, ledger[0].Delta);
        Assert.Equal(CreditTransactionReason.Consume, ledger[0].Reason);
        Assert.Equal(userId, ledger[0].OwnerId);
        Assert.Null(ledger[0].OrderId);
    }

    // (b) gọi 2 lần cùng sessionId → chỉ trừ 1: đúng 1 transaction, lần 2 AlreadyFinalized (no-op).
    [Fact]
    public async Task Consume_GoiLai2Lan_ChiTru1Transaction()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 3);
        await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        var first = await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);
        var second = await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);

        Assert.Equal(ConsumeOutcome.Consumed, first.Outcome);
        Assert.Equal(ConsumeOutcome.AlreadyFinalized, second.Outcome);   // absorbing PAY-11

        using var read = tdb.NewContext();
        Assert.Equal(1, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId)); // đúng 1
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(2, acc.RemainingCredits);   // reserve trừ 1, consume không đụng remaining
        Assert.Equal(0, acc.ReservedCredits);    // giảm đúng 1 lần
    }

    // (c1) reservation đã Released (bỏ ngang trước) → consume no-op: KHÔNG bút toán, số dư nguyên.
    [Fact]
    public async Task Consume_ReservationDaReleased_NoOp()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        // mô phỏng sau release: remaining hoàn lại, reserved=0, reservation=Released.
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 4, reserved: 0);
        await SeedReservationAsync(tdb, OwnerType.Org, orgId, sessionId, ReservationStatus.Released);

        var result = await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);

        Assert.Equal(ConsumeOutcome.AlreadyFinalized, result.Outcome);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId)); // không trừ oan
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(4, acc.RemainingCredits);
        Assert.Equal(0, acc.ReservedCredits);
        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Released, reservation.Status); // vẫn Released
    }

    // (c2) reservation chưa tồn tại (miss event reserve) → consume no-op: NoReservation, KHÔNG bút toán.
    [Fact]
    public async Task Consume_KhongCoReservation_NoOp()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 2);

        var result = await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);

        Assert.Equal(ConsumeOutcome.NoReservation, result.Outcome);
        Assert.Null(result.ReservationId);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(2, acc.RemainingCredits); // ví nguyên vẹn
        Assert.Equal(0, acc.ReservedCredits);
    }

    // ── BK7 — Postpaid: consume dồn nợ kỳ (payment.md §Kế toán POSTPAID · period_usage += 1) ─────────

    // (BK7-a) postpaid session Scored → consume: reserved−1, period_usage += 1 (dồn nợ kỳ), đúng 1
    //         credit_transactions(Consume, −1); remaining vẫn 0 (postpaid không dùng remaining).
    [Fact]
    public async Task Consume_Postpaid_CongPeriodUsage_GhiLedger()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: 5, periodUsage: 0);

        // reserve trước (state thật postpaid): reserved 0→1, period_usage KHÔNG đổi (chỉ consume mới cộng).
        await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        var result = await new CreditAccountService(tdb.NewContext())
            .ConsumeAsync(sessionId);

        Assert.Equal(ConsumeOutcome.Consumed, result.Outcome);

        using var read = tdb.NewContext();
        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Consumed, reservation.Status);

        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(0, acc.RemainingCredits);   // postpaid không dùng remaining
        Assert.Equal(0, acc.ReservedCredits);    // reserved −1
        Assert.Equal(1, acc.PeriodUsage);        // nợ kỳ += 1 (nguồn interview_count)

        var ledger = await read.CreditTransactions.Where(t => t.SessionId == sessionId).ToListAsync();
        Assert.Single(ledger);
        Assert.Equal(-1, ledger[0].Delta);
        Assert.Equal(CreditTransactionReason.Consume, ledger[0].Reason);
        Assert.Equal(orgId, ledger[0].OwnerId);
    }

    // (BK7-b) gọi 2 lần cùng session → period_usage chỉ +1 (idempotent/absorbing PAY-11), đúng 1 ledger.
    [Fact]
    public async Task Consume_Postpaid_GoiLai2Lan_PeriodUsageChiCong1()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: 5, periodUsage: 3);
        await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        var first = await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);
        var second = await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);

        Assert.Equal(ConsumeOutcome.Consumed, first.Outcome);
        Assert.Equal(ConsumeOutcome.AlreadyFinalized, second.Outcome);   // absorbing PAY-11

        using var read = tdb.NewContext();
        Assert.Equal(1, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId)); // đúng 1
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(4, acc.PeriodUsage);        // 3 (seed) + 1, KHÔNG cộng lần 2
        Assert.Equal(0, acc.ReservedCredits);    // giảm đúng 1 lần
    }

    // (BK7-c) prepaid consume → period_usage KHÔNG đổi (giữ null) — không hồi quy P5.
    [Fact]
    public async Task Consume_Prepaid_KhongDongDenPeriodUsage()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5); // prepaid: period_usage = null

        await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);

        var result = await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);

        Assert.Equal(ConsumeOutcome.Consumed, result.Outcome);
        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Null(acc.PeriodUsage);            // prepaid KHÔNG chạm period_usage
        Assert.Equal(4, acc.RemainingCredits);   // reserve −1, consume không đụng remaining
        Assert.Equal(0, acc.ReservedCredits);
    }
}
