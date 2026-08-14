using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// PP1 — bất biến sổ cái <c>remaining + reserved = Σ credit_transactions.delta</c> phải đứng vững với
/// CẢ ví postpaid.
///
/// Trước PP1, <c>ConsumeAsync</c> ghi <c>Consume −1</c> cho cả hai mode, nhưng ví postpaid không bao
/// giờ có bút toán DƯƠNG đối ứng: reserve postpaid không ghi sổ, và tất toán hoá đơn cố ý KHÔNG cộng
/// credit. Kết quả là <c>0 = −N</c>, âm dần vĩnh viễn — phá đúng máy dò lệch số dư duy nhất của hệ
/// thống, và làm mọi drift THẬT trên ví postpaid trở nên không phát hiện được vì baseline đã sai.
///
/// Hai nhánh "không phải credit" khác (<c>Subscription</c>, <c>InvoiceSettlement</c>) vốn đã cố ý
/// không ghi sổ cái vì đúng lý do này — postpaid là chỗ DUY NHẤT còn bất nhất.
/// </summary>
public class PostpaidLedgerInvariantTests
{
    private static async Task SeedAsync(PaymentTestDb tdb, Guid orgId, PaymentMode mode, int creditLimit = 10)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = mode,
            Status = CreditAccountStatus.Active,
            RemainingCredits = mode == PaymentMode.Prepaid ? 5 : 0,
            ReservedCredits = 0,
            CreditLimit = mode == PaymentMode.Postpaid ? creditLimit : null,
            PeriodUsage = mode == PaymentMode.Postpaid ? 0 : null,
            UpdatedAt = DateTime.UtcNow,
        });
        if (mode == PaymentMode.Prepaid)
            tdb.Db.CreditTransactions.Add(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                OwnerType = OwnerType.Org,
                OwnerId = orgId,
                Delta = 5,
                Reason = CreditTransactionReason.Purchase,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            });
        await tdb.Db.SaveChangesAsync();
    }

    private static async Task<(int Balance, int Delta)> ReadAsync(PaymentTestDb tdb, Guid orgId)
    {
        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == orgId);
        var delta = await read.CreditTransactions.AsNoTracking()
            .Where(t => t.OwnerType == OwnerType.Org && t.OwnerId == orgId)
            .SumAsync(t => (int?)t.Delta) ?? 0;
        return (acc.RemainingCredits + acc.ReservedCredits, delta);
    }

    /// <summary>Ba lượt tiêu liên tiếp: bất biến phải giữ sau MỖI lượt, không chỉ lượt đầu.</summary>
    [Fact]
    public async Task Postpaid_TieuNhieuLuot_BatBienSoCaiGiuNguyen()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, PaymentMode.Postpaid);

        for (var i = 0; i < 3; i++)
        {
            var sessionId = Guid.NewGuid();
            await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, sessionId);
            await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);

            var (balance, delta) = await ReadAsync(tdb, orgId);
            Assert.Equal(delta, balance);   // trước PP1: 0 == −(i+1) ⇒ gãy ngay lượt đầu
        }

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(3, acc.PeriodUsage);   // nợ kỳ VẪN được ghi — chỉ đổi CHỖ ghi, không mất
        Assert.Empty(await read.CreditTransactions.AsNoTracking().Where(t => t.OwnerId == orgId).ToListAsync());
    }

    /// <summary>Bỏ ngang (release) cũng không được làm lệch bất biến.</summary>
    [Fact]
    public async Task Postpaid_TieuRoiBoNgang_BatBienVanGiu()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, PaymentMode.Postpaid);

        var consumed = Guid.NewGuid();
        await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, consumed);
        await new CreditAccountService(tdb.NewContext()).ConsumeAsync(consumed);

        var released = Guid.NewGuid();
        await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, released);
        await new CreditAccountService(tdb.NewContext()).ReleaseAsync(released);

        var (balance, delta) = await ReadAsync(tdb, orgId);
        Assert.Equal(delta, balance);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(1, acc.PeriodUsage);   // bỏ ngang KHÔNG dồn nợ (P8a) — chỉ lượt tiêu thật mới tính
        Assert.Equal(0, acc.ReservedCredits);
    }

    /// <summary>Chống hồi quy: ví PREPAID vẫn phải ghi sổ cái như cũ, và bất biến vẫn đúng.</summary>
    [Fact]
    public async Task Prepaid_VanGhiSoCai_VaBatBienVanDung()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, PaymentMode.Prepaid);

        var sessionId = Guid.NewGuid();
        await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, sessionId);
        await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);

        using var read = tdb.NewContext();
        var consume = Assert.Single(await read.CreditTransactions.AsNoTracking()
            .Where(t => t.SessionId == sessionId).ToListAsync());
        Assert.Equal(-1, consume.Delta);
        Assert.Equal(CreditTransactionReason.Consume, consume.Reason);

        var (balance, delta) = await ReadAsync(tdb, orgId);
        Assert.Equal(delta, balance);       // 4 + 0 == 5 − 1
        Assert.Equal(4, balance);
    }
}
