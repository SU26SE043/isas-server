using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using PaymentService.Models;
using static Isas.PaymentService.Services.IInvoiceService;

namespace Isas.PaymentService.Tests;

/// <summary>
/// PP2 — chốt kỳ postpaid TỰ ĐỘNG, kèm 2 guard mà chốt-tay cũng hưởng.
///
/// Trước vòng này KHÔNG BackgroundService nào gọi <c>CloseBillingPeriodAsync</c> (11 reconciler trong
/// Payment, 0 cái chốt kỳ) ⇒ "hoá đơn cuối kỳ" là việc bấm tay từng org. Quên chốt một tháng thì
/// <c>period_usage</c> cứ tăng tới <c>credit_limit</c> rồi org bị 402 vĩnh viễn **mà không có hoá đơn
/// nào để trả** — hỏng câm.
///
/// Hai guard là ĐIỀU KIỆN để tự động hoá an toàn, không phải hardening rời:
/// thiếu <c>AlreadyClosed</c> thì job quét lại cùng kỳ sẽ **nhân bản hoá đơn mỗi vòng**;
/// thiếu <c>NothingToBill</c> thì mỗi tháng mọi org không dùng gì đều nhận **hoá đơn 0 đồng**.
/// </summary>
public class BillingCloseAutoTests
{
    private const decimal UnitPrice = 2_000m;

    // PP2 không đụng đường tất toán → order service chỉ cần tồn tại, không cần hành vi.
    private static InvoiceService NewService(PaymentTestDb tdb) =>
        new(tdb.NewContext(), new Mock<IOrderService>().Object,
            Options.Create(new BillingSettings { UnitPrice = UnitPrice }));

    private static async Task SeedAsync(PaymentTestDb tdb, Guid orgId, PaymentMode mode, int periodUsage)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = mode,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            ReservedCredits = 0,
            CreditLimit = mode == PaymentMode.Postpaid ? 100 : null,
            PeriodUsage = mode == PaymentMode.Postpaid ? periodUsage : null,
            UpdatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();
    }

    // ── Hai guard (áp cho CẢ chốt tay lẫn chốt tự động) ──────────────────────────────────────

    [Fact]
    public async Task ChotKy_KhongCoLuotDung_KhongLapHoaDonKhongDong()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, PaymentMode.Postpaid, periodUsage: 0);

        var result = await NewService(tdb).CloseBillingPeriodAsync(orgId);

        Assert.Equal(CloseBillingPeriodOutcome.NothingToBill, result.Outcome);
        Assert.Empty(await tdb.NewContext().Invoices.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ChotKy_GoiHaiLanCungKy_LanHaiKhongTaoThemHoaDon()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, PaymentMode.Postpaid, periodUsage: 7);
        var pStart = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var pEnd = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var first = await NewService(tdb).CloseBillingPeriodAsync(orgId, pStart, pEnd);
        var second = await NewService(tdb).CloseBillingPeriodAsync(orgId, pStart, pEnd);

        Assert.Equal(CloseBillingPeriodOutcome.Closed, first.Outcome);
        Assert.Equal(CloseBillingPeriodOutcome.AlreadyClosed, second.Outcome);
        var invoices = await tdb.NewContext().Invoices.AsNoTracking().ToListAsync();
        Assert.Single(invoices);
        Assert.Equal(7, invoices[0].InterviewCount);
        Assert.Equal(7 * UnitPrice, invoices[0].Amount);
    }

    // ── Chốt tự động ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChotKyTuDong_LapHoaDonChoDungThangVuaKetThuc()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, PaymentMode.Postpaid, periodUsage: 3);

        // Đứng giữa tháng 3 → kỳ phải chốt là THÁNG 2, không phải tháng đang chạy.
        var closed = await NewService(tdb).CloseDuePeriodsAsync(new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, closed);
        var inv = Assert.Single(await tdb.NewContext().Invoices.AsNoTracking().ToListAsync());
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), inv.PeriodStart);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), inv.PeriodEnd);
        Assert.Equal(InvoiceStatus.Issued, inv.Status);
    }

    [Fact]
    public async Task ChotKyTuDong_ChayLaiCungKy_KhongNhanBanHoaDon()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(tdb, orgId, PaymentMode.Postpaid, periodUsage: 4);
        var asOf = new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc);

        var first = await NewService(tdb).CloseDuePeriodsAsync(asOf);
        var second = await NewService(tdb).CloseDuePeriodsAsync(asOf);

        Assert.Equal(1, first);
        Assert.Equal(0, second);   // vòng quét sau KHÔNG lập thêm
        Assert.Single(await tdb.NewContext().Invoices.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ChotKyTuDong_BoQuaViPrepaid()
    {
        using var tdb = new PaymentTestDb();
        await SeedAsync(tdb, Guid.NewGuid(), PaymentMode.Prepaid, periodUsage: 0);

        var closed = await NewService(tdb).CloseDuePeriodsAsync(new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, closed);
        Assert.Empty(await tdb.NewContext().Invoices.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ChotKyTuDong_ChiDemOrgThucSuCoNo()
    {
        using var tdb = new PaymentTestDb();
        var coNo = Guid.NewGuid();
        var khongDung = Guid.NewGuid();
        await SeedAsync(tdb, coNo, PaymentMode.Postpaid, periodUsage: 2);
        await SeedAsync(tdb, khongDung, PaymentMode.Postpaid, periodUsage: 0);

        var closed = await NewService(tdb).CloseDuePeriodsAsync(new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, closed);
        var inv = Assert.Single(await tdb.NewContext().Invoices.AsNoTracking().ToListAsync());
        Assert.Equal(coNo, inv.OwnerId);   // org không dùng gì KHÔNG nhận hoá đơn 0 đồng
    }

    /// <summary>Job mặc định TẮT — bật = hệ thống tự lập hoá đơn thật, phải là quyết định vận hành riêng.</summary>
    [Fact]
    public void JobChotKy_MacDinh_TAT()
    {
        var s = new BillingCloseSettings();
        Assert.False(s.Enabled);
        Assert.True(s.ScanIntervalSeconds > 0);
    }
}
