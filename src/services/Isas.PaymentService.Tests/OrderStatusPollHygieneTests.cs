using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// P3 poll hygiene — (a) bằng chứng poll chỉ ghi khi trạng thái PayOS ĐỔI (đo prod: 404/427 dòng
/// payment_transactions là rác poll "pending" lặp, 1 đơn 122 dòng); (b) PayOS báo link Cancelled → đóng
/// đơn Cancelled ngay tại poll (link chết, PayOS không nhận tiền nữa) thay vì Pending tới sweeper ~45';
/// (c) CHỈ Cancelled — Expired/Failed/Processing vẫn giữ Pending cho sweeper (đóng mù = mất tiền);
/// (d) guard WHERE status=Pending: webhook Paid thắng cuộc đua thì KHÔNG bị ghi đè thành Cancelled.
/// </summary>
public class OrderStatusPollHygieneTests
{
    private sealed class StubPayOs : IPayOsQueryClient
    {
        private readonly Queue<PayOsPaymentStatus> _statuses;
        private readonly Func<Task>? _sideEffect;
        public int CallCount { get; private set; }
        public StubPayOs(IEnumerable<PayOsPaymentStatus> statuses, Func<Task>? sideEffect = null)
        {
            _statuses = new Queue<PayOsPaymentStatus>(statuses);
            _sideEffect = sideEffect;
        }
        public async Task<PayOsPaymentInfo> GetPaymentInfoAsync(long orderCode, CancellationToken ct = default)
        {
            CallCount++;
            if (_sideEffect is not null) await _sideEffect();
            var st = _statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek();
            return new PayOsPaymentInfo(st, "FTPOLL", "{\"status\":\"" + st.ToString().ToUpperInvariant() + "\"}");
        }
    }

    private static OrderStatusService NewService(PaymentTestDb tdb, IPayOsQueryClient payos)
    {
        var ctx = tdb.NewContext();
        var webhooks = new WebhookService(ctx, new CreditAccountService(ctx));
        return new OrderStatusService(ctx, payos, webhooks, NullLogger<OrderStatusService>.Instance);
    }

    private static async Task<(Order Order, Guid OwnerId)> SeedPendingAsync(PaymentTestDb tdb, long code)
    {
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(), Name = "Pack", Type = PackageType.OneTime, PriceVnd = 100_000,
            InterviewCredits = 5, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        var ownerId = Guid.NewGuid();
        var order = new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = ownerId, Kind = OrderKind.CreditPack,
            PackageId = pkg.Id, Status = OrderStatus.Pending, AmountVnd = 100_000, PayosOrderCode = code,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30), CreatedAt = DateTime.UtcNow
        };
        tdb.Db.ProductPackages.Add(pkg);
        tdb.Db.Orders.Add(order);
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = ownerId, PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active, RemainingCredits = 0, ReservedCredits = 0, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
        return (order, ownerId);
    }

    // (a) 3 lượt poll cùng trạng thái PayOS → đúng 1 dòng bằng chứng (không phải 3).
    [Fact]
    public async Task Poll3Lan_CungTrangThai_Chi1DongBangChung()
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedPendingAsync(tdb, 260915100001);
        var payos = new StubPayOs([PayOsPaymentStatus.Pending]);

        for (var i = 0; i < 3; i++)
        {
            var r = await NewService(tdb, payos).GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);
            Assert.Equal(OrderStatus.Pending, r!.Status);
        }

        Assert.Equal(3, payos.CallCount);
        using var read = tdb.NewContext();
        var evidence = await read.PaymentTransactions.Where(t => t.OrderId == order.Id).ToListAsync();
        Assert.Single(evidence);
        Assert.Equal("pending", evidence[0].Status);
    }

    // (a') trạng thái ĐỔI (pending → processing) thì mỗi chuyển tiếp 1 dòng.
    [Fact]
    public async Task Poll_TrangThaiDoi_MoiChuyenTiep1Dong()
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedPendingAsync(tdb, 260915100002);
        var payos = new StubPayOs([PayOsPaymentStatus.Pending, PayOsPaymentStatus.Processing]);

        await NewService(tdb, payos).GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);
        await Task.Delay(5);   // CreatedAt phân biệt được thứ tự
        await NewService(tdb, payos).GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);
        await Task.Delay(5);
        await NewService(tdb, payos).GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);

        using var read = tdb.NewContext();
        var evidence = await read.PaymentTransactions.Where(t => t.OrderId == order.Id)
            .OrderBy(t => t.CreatedAt).Select(t => t.Status).ToListAsync();
        Assert.Equal(new[] { "pending", "processing" }, evidence);
    }

    // (b) PayOS Cancelled → đơn Cancelled NGAY + result Cancelled; poll lần 2 terminal → KHÔNG gọi PayOS.
    [Fact]
    public async Task PayOsCancelled_DongDonCancelledNgay_LanSauKhongGoiPayOs()
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedPendingAsync(tdb, 260915100003);
        var payos = new StubPayOs([PayOsPaymentStatus.Cancelled]);

        var first = await NewService(tdb, payos).GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);
        var second = await NewService(tdb, payos).GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);

        Assert.Equal(OrderStatus.Cancelled, first!.Status);
        Assert.Null(first.PaidAt);
        Assert.Equal(OrderStatus.Cancelled, second!.Status);
        Assert.Equal(1, payos.CallCount);

        using var read = tdb.NewContext();
        var o = await read.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal(OrderStatus.Cancelled, o.Status);
        Assert.Null(o.PaidAt);
        Assert.Equal(0, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits);
        Assert.Equal(0, await read.CreditTransactions.CountAsync());
        var evidence = await read.PaymentTransactions.Where(t => t.OrderId == order.Id).ToListAsync();
        Assert.Single(evidence);
        Assert.Equal("cancelled", evidence[0].Status);
    }

    // (c) CHỈ Cancelled đóng ngay — Expired/Failed/Processing phía PayOS vẫn giữ Pending cho sweeper.
    [Theory]
    [InlineData(PayOsPaymentStatus.Expired)]
    [InlineData(PayOsPaymentStatus.Failed)]
    [InlineData(PayOsPaymentStatus.Processing)]
    [InlineData(PayOsPaymentStatus.Underpaid)]
    public async Task PayOsKhacCancelled_GiuPending(PayOsPaymentStatus status)
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedPendingAsync(tdb, 260915100010 + (long)status);
        var payos = new StubPayOs([status]);

        var r = await NewService(tdb, payos).GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);

        Assert.Equal(OrderStatus.Pending, r!.Status);
        using var read = tdb.NewContext();
        Assert.Equal(OrderStatus.Pending, (await read.Orders.SingleAsync(x => x.Id == order.Id)).Status);
    }

    // (d) Đua: webhook Paid chốt đơn TRONG LÚC poll đang hỏi PayOS (PayOS trả Cancelled — bản cũ) →
    // guard WHERE status=Pending 0 row → KHÔNG ghi đè Paid, trả Paid + PaidAt.
    [Fact]
    public async Task PayOsCancelled_NhungWebhookPaidThangTruoc_KhongGhiDe()
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedPendingAsync(tdb, 260915100004);
        var payos = new StubPayOs([PayOsPaymentStatus.Cancelled], sideEffect: async () =>
        {
            using var other = tdb.NewContext();
            var svc = new WebhookService(other, new CreditAccountService(other));
            Assert.Equal(WebhookApplyOutcome.Credited, await svc.ApplyPaidWebhookAsync(order.PayosOrderCode, "FTWH", "{}"));
        });

        var r = await NewService(tdb, payos).GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);

        Assert.Equal(OrderStatus.Paid, r!.Status);
        Assert.NotNull(r.PaidAt);
        using var read = tdb.NewContext();
        var o = await read.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal(OrderStatus.Paid, o.Status);
        Assert.Equal(5, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits);
    }
}
