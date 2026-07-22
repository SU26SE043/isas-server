using Isas.PaymentService.DTOs;
using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayOS;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// F18 (mở rộng) — theo dõi "đã chuyển tiền hoàn cho khách". PayOS KHÔNG có API refund ⇒ tiền về bank phải
/// làm tay; refund chỉ lật đơn→Refunded + thu hồi credit NGAY, còn chuyển tiền là bước tay riêng, có thể quên.
/// <c>refund_settled_at</c> phân biệt "đã hoàn nhưng CHỜ chuyển tiền" (NULL) với "đã chuyển" (có mốc).
///
/// Settle KHÔNG đụng credit/status — chỉ là mốc đối soát dòng tiền ra; các bất biến sổ cái không liên quan.
/// </summary>
public class RefundSettlementTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private static RefundService NewRefund(PaymentTestDb tdb) => new(tdb.Db);

    private static OrderService NewOrders(PaymentTestDb tdb) =>
        new(tdb.Db,
            new PayOSClient(new PayOSOptions { ClientId = "x", ApiKey = "x", ChecksumKey = "x" }),
            Options.Create(new PayOSSettings()),
            new OrderCodeGenerator(tdb.Db));

    // Seed 1 đơn ở trạng thái tuỳ ý (refund/settled state tường minh) — settle không cần ví/ledger.
    private static async Task<Order> SeedOrderAsync(
        PaymentTestDb tdb, OrderStatus status, long orderCode,
        DateTime? refundedAt = null, DateTime? settledAt = null, string? gatewayRef = null)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = Guid.NewGuid(),
            Kind = OrderKind.CreditPack,
            PackageId = null,
            Status = status,
            AmountVnd = 100_000,
            PayosOrderCode = orderCode,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            PaidAt = status is OrderStatus.Paid or OrderStatus.Refunded ? DateTime.UtcNow.AddMinutes(-5) : null,
            RefundedAt = refundedAt,
            RefundSettledAt = settledAt,
            RefundGatewayRef = gatewayRef,
            CreatedAt = DateTime.UtcNow,
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    // Seed 1 đơn Paid + ví + bút toán mua để RefundOrderAsync chạy trọn (dùng cho test settledNow).
    private static async Task<Order> SeedPaidWithWalletAsync(PaymentTestDb tdb, int credits = 5)
    {
        var ownerId = Guid.NewGuid();
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = credits,
            UpdatedAt = DateTime.UtcNow,
        });
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            Kind = OrderKind.CreditPack,
            Status = OrderStatus.Paid,
            AmountVnd = 500_000,
            PayosOrderCode = Random.Shared.NextInt64(1, long.MaxValue / 4),
            ExpiredAt = DateTime.UtcNow.AddHours(1),
            PaidAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();

        tdb.Db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            OrderId = order.Id,
            Delta = credits,
            Reason = CreditTransactionReason.Purchase,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
        });
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    // ── settle ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Settle_DonChuaRefunded_TraNotRefunded_KhongDatMoc()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedOrderAsync(tdb, OrderStatus.Paid, 3001);

        var r = await NewRefund(tdb).SettleRefundAsync(order.Id, Admin, "REF-1");

        Assert.Equal(SettleOutcome.NotRefunded, r.Outcome);
        await using var db = tdb.NewContext();
        Assert.Null((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).RefundSettledAt);
    }

    [Fact]
    public async Task Settle_DonKhongTonTai_TraOrderNotFound()
    {
        using var tdb = new PaymentTestDb();
        var r = await NewRefund(tdb).SettleRefundAsync(Guid.NewGuid(), Admin, null);
        Assert.Equal(SettleOutcome.OrderNotFound, r.Outcome);
    }

    [Fact]
    public async Task Settle_DonRefundedChuaChuyen_DatMoc_GhiRef()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedOrderAsync(
            tdb, OrderStatus.Refunded, 3002, refundedAt: DateTime.UtcNow.AddMinutes(-2));

        var r = await NewRefund(tdb).SettleRefundAsync(order.Id, Admin, "PAYOS-REF-9");

        Assert.Equal(SettleOutcome.Settled, r.Outcome);
        Assert.NotNull(r.RefundSettledAt);
        Assert.Equal("PAYOS-REF-9", r.RefundGatewayRef);

        await using var db = tdb.NewContext();
        var saved = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.NotNull(saved.RefundSettledAt);
        Assert.Equal("PAYOS-REF-9", saved.RefundGatewayRef);
        Assert.Equal(OrderStatus.Refunded, saved.Status);   // status KHÔNG đổi
    }

    [Fact]
    public async Task Settle_HaiLan_Idempotent_KhongDoiMocCu_KhongXoaRef()
    {
        using var tdb = new PaymentTestDb();
        var firstSettled = DateTime.UtcNow.AddMinutes(-1);
        var order = await SeedOrderAsync(
            tdb, OrderStatus.Refunded, 3003,
            refundedAt: DateTime.UtcNow.AddMinutes(-3), settledAt: firstSettled, gatewayRef: "FIRST-REF");

        // Bấm settle lần hai với ref khác → phải idempotent: giữ mốc đầu + ref đầu.
        var r = await NewRefund(tdb).SettleRefundAsync(order.Id, Admin, "SECOND-REF");

        Assert.Equal(SettleOutcome.AlreadySettled, r.Outcome);

        await using var db = tdb.NewContext();
        var saved = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(firstSettled, saved.RefundSettledAt);   // mốc gốc không dời
        Assert.Equal("FIRST-REF", saved.RefundGatewayRef);   // ref cũ không bị đè
    }

    [Fact]
    public async Task Settle_KhongTruyenRef_GiuNguyenRefCu()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedOrderAsync(
            tdb, OrderStatus.Refunded, 3004,
            refundedAt: DateTime.UtcNow.AddMinutes(-2), gatewayRef: "KEEP-ME");

        var r = await NewRefund(tdb).SettleRefundAsync(order.Id, Admin, null);

        Assert.Equal(SettleOutcome.Settled, r.Outcome);
        await using var db = tdb.NewContext();
        var saved = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.NotNull(saved.RefundSettledAt);
        Assert.Equal("KEEP-ME", saved.RefundGatewayRef);     // null KHÔNG xoá ref có sẵn
    }

    // ── refund settledNow ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refund_SettledNowTrue_DatMocSettled_BangRefundedAt()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedPaidWithWalletAsync(tdb);

        var r = await NewRefund(tdb).RefundOrderAsync(order.Id, Admin, "lý do", "REF", false, settledNow: true);

        Assert.Equal(RefundOutcome.Refunded, r.Outcome);
        Assert.NotNull(r.RefundSettledAt);

        await using var db = tdb.NewContext();
        var saved = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.NotNull(saved.RefundSettledAt);
        Assert.Equal(saved.RefundedAt, saved.RefundSettledAt);   // settled = lúc hoàn
    }

    [Fact]
    public async Task Refund_SettledNowFalse_MocSettledNull_ChoChuyenTien()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedPaidWithWalletAsync(tdb);

        var r = await NewRefund(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null, false, settledNow: false);

        Assert.Equal(RefundOutcome.Refunded, r.Outcome);
        Assert.Null(r.RefundSettledAt);

        await using var db = tdb.NewContext();
        Assert.Null((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).RefundSettledAt);
    }

    // ── filter admin list ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_Pending_ChiTraDonHoanChuaChuyen()
    {
        using var tdb = new PaymentTestDb();
        var refundedAt = DateTime.UtcNow.AddMinutes(-5);
        var pending = await SeedOrderAsync(tdb, OrderStatus.Refunded, 4001, refundedAt: refundedAt);           // chờ
        await SeedOrderAsync(tdb, OrderStatus.Refunded, 4002, refundedAt: refundedAt, settledAt: refundedAt);  // đã chuyển
        await SeedOrderAsync(tdb, OrderStatus.Paid, 4003);                                                     // chưa hoàn

        var page = await NewOrders(tdb).ListAllOrdersAsync(
            null, null, RefundSettlementFilter.Pending, null, null);

        Assert.Single(page.Items);
        Assert.Equal(pending.Id, page.Items[0].Id);
        Assert.Null(page.Items[0].RefundSettledAt);
    }

    [Fact]
    public async Task Filter_Settled_ChiTraDonHoanDaChuyen()
    {
        using var tdb = new PaymentTestDb();
        var refundedAt = DateTime.UtcNow.AddMinutes(-5);
        await SeedOrderAsync(tdb, OrderStatus.Refunded, 4101, refundedAt: refundedAt);                          // chờ
        var settled = await SeedOrderAsync(tdb, OrderStatus.Refunded, 4102, refundedAt: refundedAt, settledAt: refundedAt);
        await SeedOrderAsync(tdb, OrderStatus.Paid, 4103);                                                      // chưa hoàn

        var page = await NewOrders(tdb).ListAllOrdersAsync(
            null, null, RefundSettlementFilter.Settled, null, null);

        Assert.Single(page.Items);
        Assert.Equal(settled.Id, page.Items[0].Id);
        Assert.NotNull(page.Items[0].RefundSettledAt);
    }

    [Fact]
    public async Task List_KhongFilter_TraCaHai_KemFieldRefund()
    {
        using var tdb = new PaymentTestDb();
        var refundedAt = DateTime.UtcNow.AddMinutes(-5);
        await SeedOrderAsync(tdb, OrderStatus.Refunded, 4201, refundedAt: refundedAt, gatewayRef: "R-1");
        await SeedOrderAsync(tdb, OrderStatus.Paid, 4202);

        var page = await NewOrders(tdb).ListAllOrdersAsync(null, null, null, null, null);

        Assert.Equal(2, page.Items.Count);
        var refunded = page.Items.Single(o => o.Status == OrderStatus.Refunded);
        Assert.Equal("R-1", refunded.RefundGatewayRef);   // field refund lộ ra ở list admin
    }
}
