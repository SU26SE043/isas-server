using Isas.PaymentService.DTOs;
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

    private static void SeedInvoice(
        PaymentTestDb tdb, Guid orgId, InvoiceStatus status, DateTime periodEnd, DateTime? dueAt = null)
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
            DueAt = dueAt,
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

    // ── Thang cảnh báo (bậc khẩn nhất thắng) ─────────────────────────────────────────────────
    // Trước vòng này worklist chỉ có Headroom/HasOverdue rời rạc — admin không có cách nào biết TRƯỚC
    // khi một buổi phỏng vấn thật bị 402 giữa chừng (BK17/F23). 4 bậc: ApproachingLimit (>80% hạn mức,
    // CHƯA có hoá đơn nào) → InvoiceIssued (hoá đơn vừa chốt, còn xa hạn) → DueSoon (Issued, DueAt trong
    // 3 ngày tới — mặc định BillingSettings.DueSoonDays) → Overdue (đã quá hạn, BK17 đang chặn reserve).

    [Fact]
    public async Task Alert_ChuaChamNguong_KhongCoHoaDon_None()
    {
        using var tdb = new PaymentTestDb();
        SeedWallet(tdb, Guid.NewGuid(), PaymentMode.Postpaid, periodUsage: 10, reserved: 0, creditLimit: 100);
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(PostpaidAlertLevel.None, row.AlertLevel);
    }

    /// <summary>Ranh giới 80%: 79/100 CHƯA tính là chạm ngưỡng — ranh giới sai 1 đơn vị là cảnh báo sớm/muộn oan.</summary>
    [Fact]
    public async Task Alert_DuoiTamMuoiPhanTramHanMuc_ChuaLaApproachingLimit()
    {
        using var tdb = new PaymentTestDb();
        SeedWallet(tdb, Guid.NewGuid(), PaymentMode.Postpaid, periodUsage: 79, reserved: 0, creditLimit: 100);
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(PostpaidAlertLevel.None, row.AlertLevel);
    }

    /// <summary>Đúng 80% (usage + reserved) → ApproachingLimit — tính CẢ chỗ đang giữ, không chỉ usage đã chốt.</summary>
    [Fact]
    public async Task Alert_ChamTamMuoiPhanTramHanMuc_TinhCaReserved_ApproachingLimit()
    {
        using var tdb = new PaymentTestDb();
        SeedWallet(tdb, Guid.NewGuid(), PaymentMode.Postpaid, periodUsage: 75, reserved: 5, creditLimit: 100);
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(PostpaidAlertLevel.ApproachingLimit, row.AlertLevel);
    }

    [Fact]
    public async Task Alert_ChuaDatHanMuc_KhongTinhApproachingLimit()
    {
        using var tdb = new PaymentTestDb();
        // creditLimit=null ⇒ không có mẫu số để so 80% — phải None, không phải "luôn cảnh báo vì null".
        SeedWallet(tdb, Guid.NewGuid(), PaymentMode.Postpaid, periodUsage: 999, creditLimit: null);
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(PostpaidAlertLevel.None, row.AlertLevel);
    }

    [Fact]
    public async Task Alert_HoaDonIssued_ConXaHan_InvoiceIssued()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        SeedWallet(tdb, orgId, PaymentMode.Postpaid, periodUsage: 1);
        SeedInvoice(tdb, orgId, InvoiceStatus.Issued, DateTime.UtcNow, dueAt: DateTime.UtcNow.AddDays(10));
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(PostpaidAlertLevel.InvoiceIssued, row.AlertLevel);
    }

    [Fact]
    public async Task Alert_HoaDonIssued_SapToiHanTrong3Ngay_DueSoon()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        SeedWallet(tdb, orgId, PaymentMode.Postpaid, periodUsage: 1);
        SeedInvoice(tdb, orgId, InvoiceStatus.Issued, DateTime.UtcNow, dueAt: DateTime.UtcNow.AddDays(2));
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(PostpaidAlertLevel.DueSoon, row.AlertLevel);
    }

    // KHÔNG có test "đúng mốc 3 ngày, tick-chính-xác" ở đây, CÓ CHỦ ĐÍCH: `now` trong sản phẩm là
    // DateTime.UtcNow đọc TƯƠI ở thời điểm gọi service (đúng quy ước mọi reconciler khác trong repo —
    // không có clock injection), luôn muộn hơn vài mili-giây so với `DateTime.UtcNow` mà test dùng để
    // tính DueAt lúc seed. Vì thế `DueAt = testNow + 3d` luôn NHỎ HƠN `serviceNow + 3d` một khoảng nhỏ,
    // và một test "seed đúng +3 ngày" sẽ pass y hệt nhau dù dùng `<=` hay `<` — KHÔNG phân biệt được hai
    // ngữ nghĩa, dù bề ngoài trông như test biên. Từng thử, mutation `<=`→`<` chạy qua test đó (xanh giả
    // — đã điều tra, không chấp nhận). Cặp test trên (2 ngày → DueSoon, 10 ngày → InvoiceIssued) đã đủ
    // xác nhận mốc nằm ĐÚNG QUANH giá trị DueSoonDays; tick chính xác không quan sát được và cũng không
    // có ý nghĩa nghiệp vụ (không ai đọc worklist đúng millisecond hoá đơn tới hạn).

    /// <summary>Overdue LUÔN thắng — kể cả khi usage cũng đang rất cao (>80%). Bậc khẩn nhất, không cộng dồn.</summary>
    [Fact]
    public async Task Alert_CoHoaDonOverdue_ThangCaKhiUsageCaoDongThoi_Overdue()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        SeedWallet(tdb, orgId, PaymentMode.Postpaid, periodUsage: 95, reserved: 0, creditLimit: 100);
        SeedInvoice(tdb, orgId, InvoiceStatus.Overdue, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(PostpaidAlertLevel.Overdue, row.AlertLevel);
    }

    /// <summary>Overdue thắng cả DueSoon: 1 hoá đơn Overdue + 1 hoá đơn Issued sắp tới hạn cùng org.</summary>
    [Fact]
    public async Task Alert_OverdueThangDueSoon_KhiCungOrgCoCaHai()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        SeedWallet(tdb, orgId, PaymentMode.Postpaid, periodUsage: 1);
        SeedInvoice(tdb, orgId, InvoiceStatus.Overdue, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedInvoice(tdb, orgId, InvoiceStatus.Issued, DateTime.UtcNow, dueAt: DateTime.UtcNow.AddDays(1));
        await tdb.Db.SaveChangesAsync();

        var row = Assert.Single(await NewService(tdb).GetPostpaidOverviewAsync());

        Assert.Equal(PostpaidAlertLevel.Overdue, row.AlertLevel);
    }

    /// <summary>Worklist sắp theo AlertLevel TRƯỚC TIÊN (không chỉ HasOverdue) — DueSoon lên trước InvoiceIssued dù nợ ít tiền hơn.</summary>
    [Fact]
    public async Task Worklist_SapTheoBacCanhBao_DueSoonLenTruocInvoiceIssued_DuNoItTienHon()
    {
        using var tdb = new PaymentTestDb();
        var sapToiHan = Guid.NewGuid();     // DueSoon, nợ ít (1 lượt)
        var conXaHan = Guid.NewGuid();      // InvoiceIssued, nợ nhiều (10 lượt)
        SeedWallet(tdb, sapToiHan, PaymentMode.Postpaid, periodUsage: 1);
        SeedWallet(tdb, conXaHan, PaymentMode.Postpaid, periodUsage: 10);
        SeedInvoice(tdb, sapToiHan, InvoiceStatus.Issued, DateTime.UtcNow, dueAt: DateTime.UtcNow.AddDays(1));
        SeedInvoice(tdb, conXaHan, InvoiceStatus.Issued, DateTime.UtcNow, dueAt: DateTime.UtcNow.AddDays(10));
        await tdb.Db.SaveChangesAsync();

        var rows = await NewService(tdb).GetPostpaidOverviewAsync();

        Assert.Equal([sapToiHan, conXaHan], rows.Select(r => r.OwnerId));
    }
}
