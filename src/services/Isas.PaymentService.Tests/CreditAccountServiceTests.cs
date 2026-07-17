using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

public class CreditAccountServiceTests
{
    // P1: tạo credit_account cho ORG + đọc lại (round-trip) — verify command của task P1.
    [Fact]
    public async Task CreateAccountAsync_Org_RoundTrip()
    {
        using var tdb = new PaymentTestDb();
        var svc = new CreditAccountService(tdb.Db);
        var orgId = Guid.NewGuid();

        var created = await svc.CreateAccountAsync(OwnerType.Org, orgId);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(OwnerType.Org, created.OwnerType);
        Assert.Equal(orgId, created.OwnerId);
        Assert.Equal(PaymentMode.Prepaid, created.PaymentMode);
        Assert.Equal(CreditAccountStatus.Active, created.Status);
        Assert.Equal(0, created.RemainingCredits);
        Assert.Equal(0, created.ReservedCredits);

        // đọc lại bằng context mới (không phải cùng ChangeTracker) → chứng minh đã persist thật
        var reread = await svc.GetAccountAsync(OwnerType.Org, orgId);
        Assert.NotNull(reread);
        Assert.Equal(created.Id, reread!.Id);
        Assert.Equal(OwnerType.Org, reread.OwnerType);
        Assert.Equal(orgId, reread.OwnerId);
    }

    // D15: User (B2C cá nhân) dùng chung schema, chỉ khác owner_type.
    [Fact]
    public async Task CreateAccountAsync_User_RoundTrip()
    {
        using var tdb = new PaymentTestDb();
        var svc = new CreditAccountService(tdb.Db);
        var userId = Guid.NewGuid();

        var created = await svc.CreateAccountAsync(OwnerType.User, userId);

        Assert.Equal(OwnerType.User, created.OwnerType);
        Assert.Equal(userId, created.OwnerId);
    }

    // UNIQUE (owner_type, owner_id) — payment.md §DB credit_accounts.
    [Fact]
    public async Task CreateAccountAsync_TrungOwner_NemLoi()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();

        await new CreditAccountService(tdb.Db).CreateAccountAsync(OwnerType.Org, orgId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CreditAccountService(tdb.NewContext()).CreateAccountAsync(OwnerType.Org, orgId));
    }

    // Smoke test schema: credit_reservations + credit_transactions insert được (3 bảng của P1).
    [Fact]
    public async Task CreditReservation_Va_CreditTransaction_InsertDuoc()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        // DB9 — reservation/transaction có FK (owner_type,owner_id)→credit_accounts → phải có ví tương ứng
        // (production: ví luôn tồn tại trước khi giữ chỗ/ghi sổ). Seed ví Org trước khi insert 2 con.
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            ReservedCredits = 0,
            UpdatedAt = DateTime.UtcNow
        });

        tdb.Db.CreditReservations.Add(new CreditReservation
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            SessionId = sessionId,
            Status = ReservationStatus.Reserved,
            CreatedAt = DateTime.UtcNow
        });

        tdb.Db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            OrderId = null,
            SessionId = sessionId,
            Delta = -1,
            Reason = CreditTransactionReason.Consume,
            CreatedAt = DateTime.UtcNow
        });

        await tdb.Db.SaveChangesAsync();

        using var read = tdb.NewContext();
        Assert.Equal(1, await read.CreditReservations.CountAsync(x => x.SessionId == sessionId));
        Assert.Equal(1, await read.CreditTransactions.CountAsync(x => x.SessionId == sessionId));
    }
}
