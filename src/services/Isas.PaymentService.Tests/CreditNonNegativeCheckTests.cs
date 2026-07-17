using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// DB1 — CHECK ở tầng DB: số dư credit KHÔNG bao giờ âm (remaining/reserved/period_usage ≥ 0,
/// period_usage nullable → NULL cho phép) + bút toán sổ cái delta ≠ 0. Chống double-spend/ghi rác
/// dù logic ứng dụng lỗi. Chạy trên SQLite (PaymentTestDb dùng snake_case khớp raw SQL của CHECK).
/// </summary>
public class CreditNonNegativeCheckTests
{
    private static CreditAccount NewAccount(int remaining = 0, int reserved = 0, int? periodUsage = null) => new()
    {
        Id = Guid.NewGuid(),
        OwnerType = OwnerType.Org,
        OwnerId = Guid.NewGuid(),
        PaymentMode = PaymentMode.Prepaid,
        Status = CreditAccountStatus.Active,
        RemainingCredits = remaining,
        ReservedCredits = reserved,
        PeriodUsage = periodUsage,
        UpdatedAt = DateTime.UtcNow
    };

    private static CreditTransaction NewTransaction(int delta) => new()
    {
        Id = Guid.NewGuid(),
        OwnerType = OwnerType.User,
        OwnerId = Guid.NewGuid(),
        Delta = delta,
        Reason = CreditTransactionReason.Purchase,
        CreatedAt = DateTime.UtcNow
    };

    // DB9 — credit_transactions có FK (owner_type,owner_id)→credit_accounts. Seed ví khớp owner của tx để
    // test vẫn kiểm ĐÚNG check delta (không vướng FK). Production: ledger luôn gắn ví đã tồn tại.
    private static CreditAccount AccountForOwner(CreditTransaction tx) => new()
    {
        Id = Guid.NewGuid(),
        OwnerType = tx.OwnerType,
        OwnerId = tx.OwnerId,
        PaymentMode = PaymentMode.Prepaid,
        Status = CreditAccountStatus.Active,
        RemainingCredits = 0,
        ReservedCredits = 0,
        PeriodUsage = null,
        UpdatedAt = DateTime.UtcNow
    };

    // ── credit_accounts: remaining/reserved/period_usage ≥ 0 (period_usage NULL cho phép) ──

    [Fact]
    public async Task RemainingCredits_Negative_ViPhamCheck()
    {
        using var t = new PaymentTestDb();
        t.Db.CreditAccounts.Add(NewAccount(remaining: -1));
        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task ReservedCredits_Negative_ViPhamCheck()
    {
        using var t = new PaymentTestDb();
        t.Db.CreditAccounts.Add(NewAccount(reserved: -1));
        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task PeriodUsage_Negative_ViPhamCheck()
    {
        using var t = new PaymentTestDb();
        t.Db.CreditAccounts.Add(NewAccount(periodUsage: -1));
        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task PeriodUsage_Null_ChoPhep()
    {
        using var t = new PaymentTestDb();
        var acc = NewAccount(remaining: 5, reserved: 0, periodUsage: null);
        t.Db.CreditAccounts.Add(acc);
        await t.Db.SaveChangesAsync();   // không ném — NULL không vi phạm CHECK

        await using var read = t.NewContext();
        var saved = await read.CreditAccounts.AsNoTracking().FirstAsync(a => a.Id == acc.Id);
        Assert.Null(saved.PeriodUsage);
        Assert.Equal(5, saved.RemainingCredits);
    }

    [Fact]
    public async Task ZeroBalances_ChoPhep()
    {
        using var t = new PaymentTestDb();
        var acc = NewAccount(remaining: 0, reserved: 0, periodUsage: 0);
        t.Db.CreditAccounts.Add(acc);
        await t.Db.SaveChangesAsync();   // 0 ≥ 0 hợp lệ

        await using var read = t.NewContext();
        Assert.True(await read.CreditAccounts.AnyAsync(a => a.Id == acc.Id));
    }

    // ── credit_transactions: delta ≠ 0 ──

    [Fact]
    public async Task Delta_Zero_ViPhamCheck()
    {
        using var t = new PaymentTestDb();
        var tx = NewTransaction(delta: 0);
        t.Db.CreditAccounts.Add(AccountForOwner(tx));  // ví tồn tại → throw đúng do check delta, không do FK
        t.Db.CreditTransactions.Add(tx);
        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(5)]    // Purchase +N
    [InlineData(-1)]   // Consume −1
    public async Task Delta_NonZero_ChoPhep(int delta)
    {
        using var t = new PaymentTestDb();
        var tx = NewTransaction(delta);
        t.Db.CreditAccounts.Add(AccountForOwner(tx));  // DB9 — ví khớp owner cho FK
        t.Db.CreditTransactions.Add(tx);
        await t.Db.SaveChangesAsync();   // ≠ 0 hợp lệ

        await using var read = t.NewContext();
        var saved = await read.CreditTransactions.AsNoTracking().FirstAsync(x => x.Id == tx.Id);
        Assert.Equal(delta, saved.Delta);
    }
}
