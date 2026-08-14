using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;
using Moq;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// PP2 (nửa sau) — worklist cho admin.
///
/// Trước vòng này admin KHÔNG có đường nào nhìn ra org nào cần chốt kỳ / sắp chạm hạn mức / đang bị
/// chặn: <c>AdminCreditsController</c> chỉ tra ví THEO TỪNG <c>ownerId</c>, mà nghiệp vụ chốt kỳ thì
/// tháng nào cũng lặp. Không có worklist thì job tự động (BillingCloseReconciler) chạy trong bóng tối —
/// không ai kiểm chứng được nó có bỏ sót org nào không.
/// </summary>
public class PostpaidOverviewTests
{
    private const decimal UnitPrice = 2_000m;

    private static InvoiceService NewService(PaymentTestDb tdb) =>
        new(tdb.NewContext(), new Mock<IOrderService>().Object,
            Options.Create(new BillingSettings { UnitPrice = UnitPrice }));

    private static void SeedWallet(PaymentTestDb tdb, Guid orgId, PaymentMode mode,
        int periodUsage = 0, int reserved = 0, int? creditLimit = 100)
        => tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = mode,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            ReservedCredits = reserved,
            CreditLimit = mode == PaymentMode.Postpaid ? creditLimit : null,
            PeriodUsage = mode == PaymentMode.Postpaid ? periodUsage : null,
            UpdatedAt = DateTime.UtcNow,
        });

    private static void SeedInvoice(PaymentTestDb tdb, Guid orgId, InvoiceStatus status, DateTime periodEnd)
        => tdb.Db.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PeriodStart = periodEnd.AddMonths(-1),
            PeriodEnd = periodEnd,
            InterviewCount = 1,
            UnitPrice = UnitPrice,
            Amount = UnitPrice,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        });

    [Fact]
    public async Task Worklist_ChiTraViOrgDangTraSau()
    {
        using var tdb = new PaymentTestDb();
        var postpaid = Guid.NewGuid();
        SeedWallet(tdb, postpaid, PaymentMode.Postpaid, periodUsage: 1);
        SeedWallet(tdb, Guid.NewGuid(), PaymentMode.Prepaid);
        await tdb.Db.SaveChangesAsync();

        var rows = await NewService(tdb).GetPostpaidOverviewAsync();

        Assert.Equal(postpaid, Assert.Single(rows).OwnerId);
    }

    [Fact]
    public async Task Worklist_TinhDungHanMucConLaiVaTienKyHienTai()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        SeedWallet(tdb, orgId, PaymentMode.Postpaid, periodUsage: 12, reserved: 3, creditLimit: 50);
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(12, row.PeriodUsage);
        Assert.Equal(3, row.ReservedCredits);
        Assert.Equal(50 - 12 - 3, row.Headroom);        // còn bao nhiêu lượt trước khi bị 402
        Assert.Equal(12 * UnitPrice, row.PendingAmountVnd);
    }

    /// <summary>Chưa đặt hạn mức ⇒ reserve luôn 402 (so sánh NULL loại row) — headroom phải là "không biết", không phải 0.</summary>
    [Fact]
    public async Task Worklist_ChuaDatHanMuc_HeadroomNull()
    {
        using var tdb = new PaymentTestDb();
        SeedWallet(tdb, Guid.NewGuid(), PaymentMode.Postpaid, periodUsage: 5, creditLimit: null);
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Null(row.Headroom);
        Assert.Null(row.CreditLimit);
    }

    [Fact]
    public async Task Worklist_DemDungHoaDonChuaTra_VaCoOverdue()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        SeedWallet(tdb, orgId, PaymentMode.Postpaid, periodUsage: 1);
        SeedInvoice(tdb, orgId, InvoiceStatus.Issued, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedInvoice(tdb, orgId, InvoiceStatus.Overdue, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedInvoice(tdb, orgId, InvoiceStatus.Paid, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(2, row.UnpaidInvoiceCount);   // Issued + Overdue, KHÔNG tính Paid
        Assert.True(row.HasOverdue);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), row.LastInvoicePeriodEnd);
    }

    [Fact]
    public async Task Worklist_ChuaTungChotKy_LastPeriodEndNull()
    {
        using var tdb = new PaymentTestDb();
        SeedWallet(tdb, Guid.NewGuid(), PaymentMode.Postpaid, periodUsage: 2);
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Null(row.LastInvoicePeriodEnd);   // "chưa từng chốt kỳ" ≠ "chốt kỳ rỗng"
        Assert.False(row.HasOverdue);
        Assert.Equal(0, row.UnpaidInvoiceCount);
    }

    /// <summary>Worklist phải sắp theo mức KHẨN: org đang bị chặn lên đầu, rồi tới org nợ nhiều tiền hơn.</summary>
    [Fact]
    public async Task Worklist_OrgBiChanLenDau_RoiToiOrgNoNhieuTien()
    {
        using var tdb = new PaymentTestDb();
        var biChan = Guid.NewGuid();
        var noNhieu = Guid.NewGuid();
        var noIt = Guid.NewGuid();
        SeedWallet(tdb, biChan, PaymentMode.Postpaid, periodUsage: 1);
        SeedWallet(tdb, noNhieu, PaymentMode.Postpaid, periodUsage: 40);
        SeedWallet(tdb, noIt, PaymentMode.Postpaid, periodUsage: 5);
        SeedInvoice(tdb, biChan, InvoiceStatus.Overdue, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await tdb.Db.SaveChangesAsync();

        var rows = await NewService(tdb).GetPostpaidOverviewAsync();

        Assert.Equal([biChan, noNhieu, noIt], rows.Select(r => r.OwnerId));
    }
}
