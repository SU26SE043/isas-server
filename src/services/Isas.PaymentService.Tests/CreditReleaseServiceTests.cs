using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// P6 — POST /internal/credits/release (khi SessionAbandoned/lỗi). Verify:
// (a) release → reservation Released + hoàn chỗ giữ (remaining trở lại, reserved−1); KHÔNG bút toán Consume.
// (b) gọi 2 lần cùng sessionId → idempotent (lần 2 no-op, số dư không hoàn kép).
// (c) reservation đã Consumed → no-op, KHÔNG hoàn oan (out-of-order absorbing PAY-11).
// (d) reservation chưa tồn tại (miss event reserve) → no-op, KHÔNG hoàn oan.
public class CreditReleaseServiceTests
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

    // (a) session đã reserve → release: reservation Released, hoàn chỗ giữ (remaining trở lại, reserved−1),
    //     và KHÔNG có bút toán nào (release không ghi credit_transactions).
    [Fact]
    public async Task Release_SessionDaReserve_HoanCho_KhongGhiLedger()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);

        // reserve trước (state thật): remaining 5→4, reserved 0→1.
        await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReleaseAsync(sessionId);

        Assert.Equal(ReleaseOutcome.Released, result.Outcome);
        Assert.NotNull(result.ReservationId);

        using var read = tdb.NewContext();
        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Released, reservation.Status);

        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(5, acc.RemainingCredits);   // hoàn: remaining trở lại 5
        Assert.Equal(0, acc.ReservedCredits);     // nhả chỗ: reserved −1

        // release KHÔNG ghi ledger (không tiêu credit) → sổ cái trống cho session này.
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId));
    }

    // (b) gọi 2 lần cùng sessionId → idempotent: lần 2 AlreadyFinalized, số dư không hoàn kép.
    [Fact]
    public async Task Release_GoiLai2Lan_Idempotent()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 3);
        await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        var first = await new CreditAccountService(tdb.NewContext()).ReleaseAsync(sessionId);
        var second = await new CreditAccountService(tdb.NewContext()).ReleaseAsync(sessionId);

        Assert.Equal(ReleaseOutcome.Released, first.Outcome);
        Assert.Equal(ReleaseOutcome.AlreadyFinalized, second.Outcome);   // absorbing PAY-11

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(3, acc.RemainingCredits);   // hoàn đúng 1 lần (không cộng kép)
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId));
    }

    // (c) reservation đã Consumed (đã tiêu thật) → release no-op: KHÔNG hoàn oan, số dư/ledger giữ nguyên.
    [Fact]
    public async Task Release_ReservationDaConsumed_KhongHoanOan()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 4);

        // reserve → consume (đã tiêu): remaining 4→3, reserved 0 (đã nhả khi consume), ledger −1.
        await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);
        await new CreditAccountService(tdb.NewContext())
            .ConsumeAsync(sessionId);

        var result = await new CreditAccountService(tdb.NewContext()).ReleaseAsync(sessionId);

        Assert.Equal(ReleaseOutcome.AlreadyFinalized, result.Outcome);

        using var read = tdb.NewContext();
        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Consumed, reservation.Status); // vẫn Consumed
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(3, acc.RemainingCredits);   // KHÔNG hoàn oan credit đã tiêu
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(1, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId)); // đúng 1 Consume
    }

    // (d) reservation chưa tồn tại (miss event reserve) → release no-op: NoReservation, KHÔNG hoàn oan.
    [Fact]
    public async Task Release_KhongCoReservation_NoOp()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 2);

        var result = await new CreditAccountService(tdb.NewContext()).ReleaseAsync(sessionId);

        Assert.Equal(ReleaseOutcome.NoReservation, result.Outcome);
        Assert.Null(result.ReservationId);
        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(2, acc.RemainingCredits); // ví nguyên vẹn (không hoàn oan)
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId));
    }
}
