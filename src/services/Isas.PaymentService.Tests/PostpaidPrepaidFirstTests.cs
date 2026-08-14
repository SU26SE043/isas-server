using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using static Isas.PaymentService.Services.IAdminCreditService;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Ví trả sau TIÊU HẾT CREDIT ĐÃ MUA trước, rồi mới dồn nợ kỳ.
///
/// Trước đây credit mua hồi trả trước bị kẹt cứng khi nâng lên Postpaid: nhánh đặt chỗ postpaid không
/// đụng <c>remaining_credits</c>, nên tiền đã trả nằm đó vô dụng — và <c>SetPaymentMode</c> phải dựng
/// hẳn guard <c>StrandedCredits</c> để cảnh báo. Nay nguồn chi trả được chốt MỘT LẦN lúc đặt chỗ và
/// đóng dấu vào <c>credit_reservations.payment_mode</c>; Consume/Release chỉ đọc lại con dấu đó nên
/// hai nhánh kế toán KHÔNG phải sửa gì.
/// </summary>
public class PostpaidPrepaidFirstTests
{
    private static async Task SeedAsync(PaymentTestDb tdb, Guid orgId, int remaining, int creditLimit = 10)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = PaymentMode.Postpaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = 0,
            CreditLimit = creditLimit,
            PeriodUsage = 0,
            UpdatedAt = DateTime.UtcNow,
        });
        if (remaining > 0)
            tdb.Db.CreditTransactions.Add(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                OwnerType = OwnerType.Org,
                OwnerId = orgId,
                Delta = remaining,
                Reason = CreditTransactionReason.Purchase,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            });
        await tdb.Db.SaveChangesAsync();
    }

    private static async Task SeedOverdueInvoiceAsync(PaymentTestDb tdb, Guid orgId)
    {
        tdb.Db.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PeriodStart = DateTime.UtcNow.AddMonths(-2),
            PeriodEnd = DateTime.UtcNow.AddMonths(-1),
            InterviewCount = 1,
            UnitPrice = 2000,
            Amount = 2000,
            Status = InvoiceStatus.Overdue,
            CreatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task ConCreditDaMua_DatCho_TruCreditVaDongDauPrepaid()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, remaining: 2);
        var sessionId = Guid.NewGuid();

        var r = await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.Reserved, r.Outcome);
        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(1, acc.RemainingCredits);   // trừ credit đã mua
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(0, acc.PeriodUsage);        // CHƯA dồn nợ kỳ
        var res = await read.CreditReservations.AsNoTracking().SingleAsync(x => x.SessionId == sessionId);
        Assert.Equal(PaymentMode.Prepaid, res.PaymentMode);   // con dấu quyết định kế toán về sau
    }

    [Fact]
    public async Task HetCreditDaMua_MoiDonNoKy_VaDongDauPostpaid()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, remaining: 0);
        var sessionId = Guid.NewGuid();

        var r = await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.Reserved, r.Outcome);
        using var read = tdb.NewContext();
        var res = await read.CreditReservations.AsNoTracking().SingleAsync(x => x.SessionId == sessionId);
        Assert.Equal(PaymentMode.Postpaid, res.PaymentMode);
    }

    /// <summary>
    /// Hành trình đầy đủ: ví trả sau còn 2 credit, chạy 3 buổi. Hai buổi đầu tiêu credit đã mua (có ghi
    /// sổ cái), buổi thứ ba mới thành nợ kỳ (không ghi sổ). Bất biến số dư phải đúng ở MỌI bước.
    /// </summary>
    [Fact]
    public async Task Ba_Buoi_HaiBuoiDauTieuCredit_BuoiBaThanhNoKy()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, remaining: 2);

        for (var i = 0; i < 3; i++)
        {
            var sessionId = Guid.NewGuid();
            await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, sessionId);
            await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);

            using var step = tdb.NewContext();
            var a = await step.CreditAccounts.AsNoTracking().SingleAsync(x => x.OwnerId == orgId);
            var delta = await step.CreditTransactions.AsNoTracking()
                .Where(t => t.OwnerId == orgId).SumAsync(t => (int?)t.Delta) ?? 0;
            Assert.Equal(delta, a.RemainingCredits + a.ReservedCredits);   // bất biến sau MỖI buổi
        }

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.AsNoTracking().SingleAsync(x => x.OwnerId == orgId);
        Assert.Equal(0, acc.RemainingCredits);
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(1, acc.PeriodUsage);        // CHỈ buổi thứ ba thành nợ kỳ
        Assert.Equal(2, await read.CreditTransactions.AsNoTracking()
            .CountAsync(t => t.OwnerId == orgId && t.Reason == CreditTransactionReason.Consume));
    }

    [Fact]
    public async Task TieuCreditCu_BoNgang_HoanLaiRemaining()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, remaining: 1);
        var sessionId = Guid.NewGuid();

        await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, sessionId);
        await new CreditAccountService(tdb.NewContext()).ReleaseAsync(sessionId);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.AsNoTracking().SingleAsync(x => x.OwnerId == orgId);
        Assert.Equal(1, acc.RemainingCredits);   // hoàn lại, KHÔNG bốc hơi
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(0, acc.PeriodUsage);        // bỏ ngang không dồn nợ
    }

    /// <summary>Phanh Overdue là phanh NỢ — tiêu credit đã trả tiền thì không sinh nợ mới nên không bị chặn.</summary>
    [Fact]
    public async Task ConCreditDaMua_KhongBiPhanhHoaDonQuaHan()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, remaining: 1);
        await SeedOverdueInvoiceAsync(tdb, orgId);

        var r = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, r.Outcome);
    }

    [Fact]
    public async Task HetCreditDaMua_CoHoaDonQuaHan_Bi402()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, remaining: 0);
        await SeedOverdueInvoiceAsync(tdb, orgId);

        var r = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.InvoiceOverdue, r.Outcome);
        Assert.Empty(await tdb.NewContext().CreditReservations.AsNoTracking().ToListAsync());  // no orphan
    }

    // ── Hệ quả ở đường duyệt mode ────────────────────────────────────────────────────────────

    private static AdminCreditService NewAdmin(PaymentTestDb tdb) =>
        new(tdb.NewContext(), new CreditAccountService(
            tdb.NewContext(), null, Options.Create(new BillingSettings { FreeTrialCredits = 0 })));

    /// <summary>Guard StrandedCredits đã gỡ: credit không còn bị kẹt nên chặn nâng mode là vô nghĩa.</summary>
    [Fact]
    public async Task NangLenPostpaid_ConCreditDaMua_KhongConBiChan()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.Org, OwnerId = orgId,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active,
            RemainingCredits = 5, ReservedCredits = 0, UpdatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();

        var r = await NewAdmin(tdb).SetPaymentModeAsync(OwnerType.Org, orgId, PaymentMode.Postpaid,
            creditLimit: 10, note: "hợp đồng", allowStrandedCredits: false, adminUserId: Guid.NewGuid());

        Assert.Equal(SetPaymentModeOutcome.Updated, r.Outcome);
        var acc = await tdb.NewContext().CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(5, acc.RemainingCredits);   // credit GIỮ NGUYÊN — nay tiêu được
    }

    /// <summary>
    /// Hạ mode khi còn buổi đang thi phải bị CHẶN. Chỗ giữ đó đóng dấu Postpaid nên khi chấm xong,
    /// Consume vẫn cộng period_usage — mà ví đã là Prepaid nên chốt kỳ trả NotPostpaid ⇒ lượt dùng
    /// đó không bao giờ được xuất hoá đơn (đo bằng probe trước khi vá: 0 hoá đơn).
    /// </summary>
    [Fact]
    public async Task HaVePrepaid_ConBuoiDangThi_BiChan()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, remaining: 0);
        await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid());

        var r = await NewAdmin(tdb).SetPaymentModeAsync(OwnerType.Org, orgId, PaymentMode.Prepaid,
            null, "ha mode", false, Guid.NewGuid());

        Assert.Equal(SetPaymentModeOutcome.UnpaidDebt, r.Outcome);
        var acc = await tdb.NewContext().CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(PaymentMode.Postpaid, acc.PaymentMode);   // ví KHÔNG bị đổi
    }
}
