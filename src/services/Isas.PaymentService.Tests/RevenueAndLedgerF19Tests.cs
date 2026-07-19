using System.Security.Claims;
using Isas.PaymentService.Controllers;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// F19 — (a) dashboard doanh thu cho PlatformAdmin và (b) chủ ví đọc được sổ cái credit của mình.
///
/// Trước vòng này service KHÔNG có endpoint tổng hợp nào (grep <c>revenue</c>/<c>Sum(</c> trong Payment
/// = 0), và KHÔNG endpoint nào đọc <c>credit_transactions</c> cho bất kỳ ai — kể cả chủ ví, tức người
/// dùng mất credit mà không tra được nó đi đâu.
/// </summary>
public class RevenueAndLedgerF19Tests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── seed ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<Order> SeedOrderAsync(
        PaymentTestDb tdb, OrderStatus status, long amount, DateTime? paidAt,
        OrderKind kind = OrderKind.CreditPack, DateTime? refundedAt = null, Guid? ownerId = null)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId ?? Guid.NewGuid(),
            Kind = kind,
            Status = status,
            AmountVnd = amount,
            PayosOrderCode = Random.Shared.NextInt64(1, long.MaxValue / 4),
            ExpiredAt = T0.AddDays(30),
            PaidAt = paidAt,
            RefundedAt = refundedAt,
            CreatedAt = paidAt ?? T0,
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    private static async Task SeedLedgerAsync(
        PaymentTestDb tdb, Guid ownerId, int delta, CreditTransactionReason reason,
        DateTime createdAt, Guid? sessionId = null, bool withAccount = true)
    {
        if (withAccount && !await tdb.Db.CreditAccounts
                .AnyAsync(a => a.OwnerType == OwnerType.User && a.OwnerId == ownerId))
        {
            tdb.Db.CreditAccounts.Add(new CreditAccount
            {
                Id = Guid.NewGuid(),
                OwnerType = OwnerType.User,
                OwnerId = ownerId,
                PaymentMode = PaymentMode.Prepaid,
                Status = CreditAccountStatus.Active,
                RemainingCredits = 100,
                UpdatedAt = DateTime.UtcNow,
            });
            await tdb.Db.SaveChangesAsync();
        }

        tdb.Db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            Delta = delta,
            Reason = reason,
            SessionId = sessionId,
            CreatedAt = createdAt,
        });
        await tdb.Db.SaveChangesAsync();
    }

    private static CreditAccountController NewLedgerController(PaymentTestDb tdb, params Claim[] claims) =>
        new(tdb.Db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                },
            },
        };

    // ── doanh thu ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DoanhThu_ChiCongDonPaid_BoQuaDonChuaTra()
    {
        using var tdb = new PaymentTestDb();
        await SeedOrderAsync(tdb, OrderStatus.Paid, 500_000, T0.AddDays(1));
        await SeedOrderAsync(tdb, OrderStatus.Paid, 300_000, T0.AddDays(2));
        await SeedOrderAsync(tdb, OrderStatus.Pending, 900_000, null);
        await SeedOrderAsync(tdb, OrderStatus.Expired, 900_000, null);
        await SeedOrderAsync(tdb, OrderStatus.Cancelled, 900_000, null);
        await SeedOrderAsync(tdb, OrderStatus.Failed, 900_000, null);
        // ⚠ Dòng QUAN TRỌNG NHẤT của test này. Bốn đơn trên đều có paid_at = null, nên chỉ riêng chúng
        // thì vị ngữ `status = Paid` là THỪA — điều kiện `paid_at != null` đã loại hết. Đơn đã hoàn (F18)
        // mới là ca duy nhất vừa có paid_at thật vừa KHÔNG được tính doanh thu gộp; thiếu nó thì test
        // mang tên "chỉ cộng đơn Paid" mà thực chất không hề kiểm vị ngữ trạng thái.
        await SeedOrderAsync(tdb, OrderStatus.Refunded, 900_000,
            paidAt: T0.AddDays(3), refundedAt: T0.AddDays(4));

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(800_000, r.GrossRevenueVnd);
        Assert.Equal(2, r.PaidOrderCount);
        Assert.Equal(-100_000, r.NetRevenueVnd);   // 800k thu − 900k hoàn, cùng kỳ
    }

    /// <summary>
    /// Kỳ là nửa mở <c>[from, to)</c>: đơn đúng mốc <c>from</c> được tính, đúng mốc <c>to</c> thì không.
    /// Nếu đóng cả hai đầu thì hai kỳ liền nhau đếm TRÙNG một đơn — tháng 7 và tháng 8 cùng nhận doanh
    /// thu của nửa đêm 31/7, và tổng năm sẽ lớn hơn tiền thật.
    /// </summary>
    [Fact]
    public async Task DoanhThu_KyLaNuaMo_KhongDemTrungGiuaHaiKyLienNhau()
    {
        using var tdb = new PaymentTestDb();
        await SeedOrderAsync(tdb, OrderStatus.Paid, 100_000, T0);                  // đúng `from` → tính
        await SeedOrderAsync(tdb, OrderStatus.Paid, 200_000, T0.AddDays(10));      // trong kỳ → tính
        await SeedOrderAsync(tdb, OrderStatus.Paid, 400_000, T0.AddDays(30));      // đúng `to`   → KHÔNG
        await SeedOrderAsync(tdb, OrderStatus.Paid, 800_000, T0.AddDays(-1));      // trước kỳ   → KHÔNG

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(300_000, r.GrossRevenueVnd);
    }

    /// <summary>
    /// Đơn được hoàn (F18) rời khỏi trạng thái Paid, nên nó KHÔNG còn nằm trong doanh thu gộp; tiền hoàn
    /// được đếm riêng theo <c>refunded_at</c>. Đếm tiền hoàn theo <c>paid_at</c> sẽ khiến một khoản hoàn
    /// hôm nay đi ngược về sửa doanh thu của kỳ ĐÃ CHỐT — báo cáo tháng trước tự đổi số.
    /// </summary>
    [Fact]
    public async Task DoanhThu_TienHoanDemTheoKyHoan_KhongSuaNguocKyDaChot()
    {
        using var tdb = new PaymentTestDb();
        // Bán tháng 7, hoàn tháng 8.
        await SeedOrderAsync(tdb, OrderStatus.Refunded, 500_000,
            paidAt: T0.AddDays(1), refundedAt: T0.AddDays(40));
        await SeedOrderAsync(tdb, OrderStatus.Paid, 200_000, T0.AddDays(2));

        var thang7 = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(31), RevenueGranularity.Day);
        var thang8 = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0.AddDays(31), T0.AddDays(62), RevenueGranularity.Day);

        // Tháng 7 chỉ còn đơn chưa hoàn.
        Assert.Equal(200_000, thang7.GrossRevenueVnd);
        Assert.Equal(0, thang7.RefundedVnd);

        // Khoản hoàn rơi vào tháng 8, làm doanh thu ròng tháng 8 âm — đúng bản chất kế toán.
        Assert.Equal(0, thang8.GrossRevenueVnd);
        Assert.Equal(500_000, thang8.RefundedVnd);
        Assert.Equal(1, thang8.RefundedOrderCount);
        Assert.Equal(-500_000, thang8.NetRevenueVnd);
    }

    /// <summary>
    /// ⚠ Bẫy được nêu thẳng trong brief: credit TẶNG không được cộng thành doanh thu. Cấu trúc bảo đảm
    /// điều đó — báo cáo đọc <c>orders</c>, còn quà chỉ ghi <c>credit_transactions</c> và không sinh đơn.
    /// Test này khoá tính chất đó lại để lần sau ai đổi báo cáo sang đọc sổ cái (nghe rất hợp lý: "credit
    /// bán ra") thì đỏ ngay — vì lúc đó quà sẽ thành doanh thu khống.
    /// </summary>
    [Fact]
    public async Task DoanhThu_KhongBaoGioCongCreditTang()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedOrderAsync(tdb, OrderStatus.Paid, 500_000, T0.AddDays(1), ownerId: owner);
        // Quà: suất dùng thử F7 — nằm trong sổ cái, KHÔNG có đơn nào đứng sau.
        await SeedLedgerAsync(tdb, owner, 3, CreditTransactionReason.FreeGrant, T0.AddDays(1));

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(500_000, r.GrossRevenueVnd);
        Assert.Equal(1, r.PaidOrderCount);
    }

    [Fact]
    public async Task DoanhThu_TachTheoLoaiDon()
    {
        using var tdb = new PaymentTestDb();
        await SeedOrderAsync(tdb, OrderStatus.Paid, 500_000, T0.AddDays(1), OrderKind.CreditPack);
        await SeedOrderAsync(tdb, OrderStatus.Paid, 100_000, T0.AddDays(1), OrderKind.CreditPack);
        await SeedOrderAsync(tdb, OrderStatus.Paid, 990_000, T0.AddDays(2), OrderKind.SubscriptionPurchase);
        await SeedOrderAsync(tdb, OrderStatus.Paid, 250_000, T0.AddDays(3), OrderKind.InvoiceSettlement);

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(1_840_000, r.GrossRevenueVnd);
        // Xếp giảm dần theo tiền — dòng to nhất lên đầu.
        Assert.Equal(OrderKind.SubscriptionPurchase, r.ByKind[0].Kind);
        Assert.Equal(990_000, r.ByKind[0].AmountVnd);

        var pack = r.ByKind.Single(k => k.Kind == OrderKind.CreditPack);
        Assert.Equal(600_000, pack.AmountVnd);
        Assert.Equal(2, pack.OrderCount);
    }

    [Fact]
    public async Task DoanhThu_GopTheoNgay_MoiNgayMotMoc()
    {
        using var tdb = new PaymentTestDb();
        await SeedOrderAsync(tdb, OrderStatus.Paid, 100_000, T0.AddDays(1).AddHours(3));
        await SeedOrderAsync(tdb, OrderStatus.Paid, 200_000, T0.AddDays(1).AddHours(20));
        await SeedOrderAsync(tdb, OrderStatus.Paid, 400_000, T0.AddDays(5));

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(2, r.Buckets.Count);
        Assert.Equal(T0.AddDays(1), r.Buckets[0].PeriodStart);   // hai đơn cùng ngày gộp lại
        Assert.Equal(300_000, r.Buckets[0].AmountVnd);
        Assert.Equal(2, r.Buckets[0].OrderCount);
        Assert.Equal(T0.AddDays(5), r.Buckets[1].PeriodStart);
        Assert.Equal(400_000, r.Buckets[1].AmountVnd);
    }

    [Fact]
    public async Task DoanhThu_GopTheoThang_MocLaDauThang()
    {
        using var tdb = new PaymentTestDb();
        await SeedOrderAsync(tdb, OrderStatus.Paid, 100_000, T0.AddDays(1));    // 07
        await SeedOrderAsync(tdb, OrderStatus.Paid, 200_000, T0.AddDays(20));   // 07
        await SeedOrderAsync(tdb, OrderStatus.Paid, 400_000, T0.AddDays(40));   // 08

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(90), RevenueGranularity.Month);

        Assert.Equal(2, r.Buckets.Count);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), r.Buckets[0].PeriodStart);
        Assert.Equal(300_000, r.Buckets[0].AmountVnd);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), r.Buckets[1].PeriodStart);
        Assert.Equal(400_000, r.Buckets[1].AmountVnd);
    }

    [Fact]
    public async Task DoanhThu_KyRong_TraSoKhongChuKhongNo()
    {
        using var tdb = new PaymentTestDb();
        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(0, r.GrossRevenueVnd);
        Assert.Equal(0, r.NetRevenueVnd);
        Assert.Empty(r.Buckets);
        Assert.Empty(r.ByKind);
    }

    // ── hợp đồng SQL của partial index (bài học DB27) ────────────────────────────────────────

    /// <summary>
    /// <c>ix_orders_paid_at</c> là partial index <c>WHERE status = 'Paid'</c>. Planner chỉ dùng được nó
    /// khi CHỨNG MINH được predicate của câu truy vấn suy ra predicate của index — nghĩa là EF phải render
    /// <c>status</c> thành LITERAL. Nếu nó render thành tham số (<c>status = @p</c>) thì Postgres hết
    /// chứng minh được và index chết TRONG IM LẶNG: index vẫn tồn tại, EXPLAIN vẫn seq scan, không có gì
    /// báo lỗi. Đọc SQL sinh từ chính câu truy vấn production để khoá hợp đồng đó lại.
    /// </summary>
    [Fact]
    public void DoanhThu_CauTruyVanRenderStatusThanhLiteral_DePartialIndexDungDuoc()
    {
        using var tdb = new PaymentTestDb();

        var sql = tdb.Db.Orders
            .Where(o => o.Status == OrderStatus.Paid && o.PaidAt != null
                        && o.PaidAt >= T0 && o.PaidAt < T0.AddDays(30))
            .ToQueryString();

        Assert.Contains("'Paid'", sql);
    }

    // ── sổ cái của chủ ví ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SoCai_ChiTraButToanCuaChinhChuVi()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        var nguoiKhac = Guid.NewGuid();

        await SeedLedgerAsync(tdb, me, 5, CreditTransactionReason.Purchase, T0.AddHours(1));
        await SeedLedgerAsync(tdb, me, -1, CreditTransactionReason.Consume, T0.AddHours(2), Guid.NewGuid());
        await SeedLedgerAsync(tdb, nguoiKhac, 99, CreditTransactionReason.Purchase, T0.AddHours(3));

        var controller = NewLedgerController(tdb, new Claim(ClaimTypes.NameIdentifier, me.ToString()));
        var result = await controller.GetMyCreditTransactionsAsync();

        var rows = Assert.IsType<List<CreditTransactionResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Delta == 99);
        // Mới nhất trước.
        Assert.Equal(-1, rows[0].Delta);
        Assert.Equal(CreditTransactionReason.Consume, rows[0].Reason);
        Assert.NotNull(rows[0].SessionId);
        Assert.Equal(5, rows[1].Delta);
    }

    /// <summary>
    /// D15 — có claim <c>org_id</c> thì sổ cái đọc là của ORG, không phải ví cá nhân người gọi. Cùng quy
    /// tắc chủ ví với <c>me/account</c>; lệch nhau sẽ khiến HR thấy số dư org mà biến động lại của mình.
    /// </summary>
    [Fact]
    public async Task SoCai_B2B_DocSoCaiCuaOrgChuKhongPhaiCuaCaNhan()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 10,
            UpdatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();

        tdb.Db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            Delta = 10,
            Reason = CreditTransactionReason.Purchase,
            CreatedAt = T0,
        });
        await tdb.Db.SaveChangesAsync();
        await SeedLedgerAsync(tdb, userId, 7, CreditTransactionReason.Purchase, T0);

        var controller = NewLedgerController(tdb,
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("org_id", orgId.ToString()));

        var result = await controller.GetMyCreditTransactionsAsync();
        var rows = Assert.IsType<List<CreditTransactionResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Single(rows);
        Assert.Equal(10, rows[0].Delta);
    }

    [Fact]
    public async Task SoCai_LocTheoLoaiButToan()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        await SeedLedgerAsync(tdb, me, 5, CreditTransactionReason.Purchase, T0.AddHours(1));
        await SeedLedgerAsync(tdb, me, -1, CreditTransactionReason.Consume, T0.AddHours(2), Guid.NewGuid());
        await SeedLedgerAsync(tdb, me, -1, CreditTransactionReason.Consume, T0.AddHours(3), Guid.NewGuid());

        var controller = NewLedgerController(tdb, new Claim(ClaimTypes.NameIdentifier, me.ToString()));
        var result = await controller.GetMyCreditTransactionsAsync(
            reason: CreditTransactionReason.Consume);

        var rows = Assert.IsType<List<CreditTransactionResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(CreditTransactionReason.Consume, r.Reason));
    }

    /// <summary>
    /// Phân trang keyset theo mẫu chung DB8: body vẫn là mảng, cursor ở header <c>X-Next-Cursor</c>,
    /// trang cuối KHÔNG có header. Đi hết hai trang phải ra đúng tập ban đầu, không trùng không sót.
    /// </summary>
    [Fact]
    public async Task SoCai_PhanTrangKeyset_KhongTrungKhongSot()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            await SeedLedgerAsync(tdb, me, -1, CreditTransactionReason.Consume,
                T0.AddHours(i), Guid.NewGuid());

        var c1 = NewLedgerController(tdb, new Claim(ClaimTypes.NameIdentifier, me.ToString()));
        var p1 = Assert.IsType<List<CreditTransactionResponse>>(
            Assert.IsType<OkObjectResult>((await c1.GetMyCreditTransactionsAsync(limit: 3)).Result).Value);

        Assert.Equal(3, p1.Count);
        var cursor = c1.Response.Headers[KeysetPaging.NextCursorHeader].ToString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var c2 = NewLedgerController(tdb, new Claim(ClaimTypes.NameIdentifier, me.ToString()));
        var p2 = Assert.IsType<List<CreditTransactionResponse>>(
            Assert.IsType<OkObjectResult>(
                (await c2.GetMyCreditTransactionsAsync(cursor: cursor, limit: 3)).Result).Value);

        Assert.Equal(2, p2.Count);
        Assert.True(string.IsNullOrEmpty(c2.Response.Headers[KeysetPaging.NextCursorHeader].ToString()));

        var ids = p1.Concat(p2).Select(r => r.Id).ToList();
        Assert.Equal(5, ids.Distinct().Count());
    }

    [Fact]
    public async Task SoCai_ChuaCoButToanNao_TraMangRong_KhongPhai404()
    {
        using var tdb = new PaymentTestDb();
        var controller = NewLedgerController(tdb,
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        var result = await controller.GetMyCreditTransactionsAsync();
        var rows = Assert.IsType<List<CreditTransactionResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Empty(rows);
    }

    /// <summary>Không có claim định danh nào ⇒ không suy được chủ ví ⇒ từ chối, KHÔNG trả sổ cái rỗng.</summary>
    [Fact]
    public async Task SoCai_KhongSuyDuocChuVi_TuChoi()
    {
        using var tdb = new PaymentTestDb();
        var controller = NewLedgerController(tdb);

        var result = await controller.GetMyCreditTransactionsAsync();

        Assert.IsType<ForbidResult>(result.Result);
    }
}
