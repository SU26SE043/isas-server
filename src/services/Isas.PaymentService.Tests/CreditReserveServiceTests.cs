using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// P4 — POST /internal/credits/reserve. Verify: (a) reserve OK → reserved+1/remaining-1;
// (b) hết credit → Insufficient (402) + KHÔNG có reservation dư; (c) idempotent theo session_id.
public class CreditReserveServiceTests
{
    private static async Task<CreditAccount> SeedAccountAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining,
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
            ReservedCredits = 0,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(acc);
        await tdb.Db.SaveChangesAsync();
        return acc;
    }

    // (a) reserve thành công → reserved+1, remaining-1, đúng 1 reservation Reserved.
    [Fact]
    public async Task Reserve_ConCredit_GiuCho_TruRemaining()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);

        Assert.Equal(ReserveOutcome.Reserved, result.Outcome);
        Assert.NotNull(result.ReservationId);
        Assert.Equal(1, result.ReservedCredits);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(4, acc.RemainingCredits);   // remaining -1
        Assert.Equal(1, acc.ReservedCredits);    // reserved +1

        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Reserved, reservation.Status);
        Assert.Equal(userId, reservation.OwnerId);
    }

    // (b) hết credit (remaining=0) → Insufficient (controller→402) + KHÔNG để lại reservation, số dư nguyên.
    [Fact]
    public async Task Reserve_HetCredit_Insufficient_KhongCoReservation()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 0);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.Insufficient, result.Outcome);
        Assert.Null(result.ReservationId);

        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(0, acc.RemainingCredits);
        Assert.Equal(0, acc.ReservedCredits);
    }

    // Không có ví → cũng Insufficient (block, không tạo session) + không đẻ reservation.
    [Fact]
    public async Task Reserve_KhongCoVi_Insufficient()
    {
        using var tdb = new PaymentTestDb();
        var sessionId = Guid.NewGuid();

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, Guid.NewGuid(), sessionId);

        Assert.Equal(ReserveOutcome.Insufficient, result.Outcome);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
    }

    // (c) idempotent theo session_id (PAY-4): gọi 2 lần cùng session → đúng 1 reservation, chỉ trừ 1 lần.
    [Fact]
    public async Task Reserve_GoiLaiCungSession_Idempotent()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);

        var first = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);
        var second = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);

        Assert.Equal(ReserveOutcome.Reserved, first.Outcome);
        Assert.Equal(ReserveOutcome.AlreadyReserved, second.Outcome);
        Assert.Equal(first.ReservationId, second.ReservationId);   // cùng reservation
        Assert.Equal(1, second.ReservedCredits);

        using var read = tdb.NewContext();
        Assert.Equal(1, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId)); // đúng 1
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(4, acc.RemainingCredits);   // chỉ trừ 1 lần
        Assert.Equal(1, acc.ReservedCredits);
    }

    // Account Suspended → chặn reserve mới (payment.md §State machine) dù còn remaining.
    [Fact]
    public async Task Reserve_AccountSuspended_Insufficient()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 3, status: CreditAccountStatus.Suspended);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.Insufficient, result.Outcome);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(3, acc.RemainingCredits); // không đụng số dư
    }

    // Hai session khác nhau trên cùng ví → trừ độc lập (2 reservation, remaining -2).
    [Fact]
    public async Task Reserve_HaiSessionKhacNhau_TruDocLap()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);
        var svc = new CreditAccountService(tdb.NewContext());

        var s1 = await svc.ReserveAsync(OwnerType.User, userId, Guid.NewGuid());
        var s2 = await svc.ReserveAsync(OwnerType.User, userId, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, s1.Outcome);
        Assert.Equal(ReserveOutcome.Reserved, s2.Outcome);
        Assert.NotEqual(s1.ReservationId, s2.ReservationId);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(3, acc.RemainingCredits);
        Assert.Equal(2, acc.ReservedCredits);
    }
}
