using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// P3 — OrderStatusService.GetOrderStatusAsync (active-polling đối soát; mock IPayOsQueryClient, KHÔNG cần PayOS thật).
// Verify:
// (1) Pending + PayOS Paid → reuse WebhookService cộng credit: order Paid + remaining += interview_credits.
// (2) Poll 2 lần (PayOS Paid cả 2) → idempotent: credit chỉ cộng 1 lần, lần 2 KHÔNG gọi PayOS (terminal).
// (3) Order đã Paid → no-op, trả Paid, KHÔNG gọi PayOS (PAY-10 bất biến).
// (4) Order id lạ → null (404); order của owner khác → null (404, không lộ đơn người khác).
// (5) Order Expired + PayOS trả Paid → KHÔNG auto-cộng credit (PAY-10) + KHÔNG gọi PayOS.
// (6) Pending + PayOS ≠ Paid → giữ Pending + lưu bằng chứng payment_transactions (append-only), KHÔNG cộng credit.
public class OrderStatusServiceTests
{
    // Stub PayOS query — trả trạng thái cấu hình sẵn + đếm số lần gọi (để assert KHÔNG gọi khi terminal).
    private sealed class StubPayOs : IPayOsQueryClient
    {
        private readonly PayOsPaymentInfo _info;
        public int CallCount { get; private set; }
        public StubPayOs(PayOsPaymentStatus status, string? txnId = "FTPOLL", string? raw = "{\"poll\":1}")
            => _info = new PayOsPaymentInfo(status, txnId, raw);

        public Task<PayOsPaymentInfo> GetPaymentInfoAsync(long orderCode, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_info);
        }
    }

    // Service + WebhookService PHẢI dùng chung 1 DbContext (WebhookService mở transaction cộng credit trên đó).
    private static OrderStatusService NewService(PaymentTestDb tdb, IPayOsQueryClient payos, out PaymentDbContext ctx)
    {
        ctx = tdb.NewContext();
        var webhooks = new WebhookService(ctx, new CreditAccountService(ctx));
        return new OrderStatusService(ctx, payos, webhooks, NullLogger<OrderStatusService>.Instance);
    }

    private static async Task<ProductPackage> SeedPackageAsync(PaymentTestDb tdb, int credits)
    {
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = $"Pack {credits}",
            Type = PackageType.OneTime,
            PriceVnd = 100_000,
            InterviewCredits = credits,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.ProductPackages.Add(pkg);
        await tdb.Db.SaveChangesAsync();
        return pkg;
    }

    private static async Task<Order> SeedOrderAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, Guid packageId, long orderCode, OrderStatus status)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            Kind = OrderKind.CreditPack,
            PackageId = packageId,
            Status = status,
            AmountVnd = 100_000,
            PayosOrderCode = orderCode,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            PaidAt = status == OrderStatus.Paid ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    private static async Task SeedAccountAsync(PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = 0,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
    }

    // (1) Pending + PayOS Paid → cộng credit qua WebhookService: order Paid + remaining += N.
    [Fact]
    public async Task Status_Pending_PayOsPaid_CongCredit_TraPaid()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        const long code = 260711130001;
        var pkg = await SeedPackageAsync(tdb, credits: 10);
        var order = await SeedOrderAsync(tdb, OwnerType.User, ownerId, pkg.Id, code, OrderStatus.Pending);
        await SeedAccountAsync(tdb, OwnerType.User, ownerId, remaining: 3);

        var payos = new StubPayOs(PayOsPaymentStatus.Paid);
        var result = await NewService(tdb, payos, out _)
            .GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);

        Assert.NotNull(result);
        Assert.Equal(code, result!.OrderCode);
        Assert.Equal(OrderStatus.Paid, result.Status);
        Assert.NotNull(result.PaidAt);
        Assert.Equal(1, payos.CallCount);

        using var read = tdb.NewContext();
        Assert.Equal(OrderStatus.Paid, (await read.Orders.SingleAsync(o => o.Id == order.Id)).Status);
        Assert.Equal(13, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits);
        Assert.Single(await read.CreditTransactions.Where(t => t.OrderId == order.Id).ToListAsync());
    }

    // (2) Poll 2 lần (PayOS Paid cả 2) → credit chỉ cộng 1 lần; lần 2 order đã terminal → KHÔNG gọi PayOS.
    [Fact]
    public async Task Status_Poll2Lan_Idempotent_KhongCong2Lan()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        const long code = 260711130002;
        var pkg = await SeedPackageAsync(tdb, credits: 5);
        var order = await SeedOrderAsync(tdb, OwnerType.Org, ownerId, pkg.Id, code, OrderStatus.Pending);
        await SeedAccountAsync(tdb, OwnerType.Org, ownerId, remaining: 0);

        var payos = new StubPayOs(PayOsPaymentStatus.Paid);
        var first = await NewService(tdb, payos, out _).GetOrderStatusAsync(order.Id, OwnerType.Org, ownerId);
        var second = await NewService(tdb, payos, out _).GetOrderStatusAsync(order.Id, OwnerType.Org, ownerId);

        Assert.Equal(OrderStatus.Paid, first!.Status);
        Assert.Equal(OrderStatus.Paid, second!.Status);
        // Poll 1 gọi PayOS (Pending→đối soát); poll 2 đơn đã Paid (terminal) → KHÔNG gọi PayOS.
        Assert.Equal(1, payos.CallCount);

        using var read = tdb.NewContext();
        Assert.Equal(5, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits); // 5, không phải 10
        Assert.Equal(1, await read.CreditTransactions.CountAsync(t => t.OrderId == order.Id));                // đúng 1 Purchase
    }

    // (3) Order đã Paid → no-op, trả Paid, KHÔNG gọi PayOS (PAY-10 bất biến).
    [Fact]
    public async Task Status_OrderDaPaid_NoOp_KhongGoiPayOs()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        const long code = 260711130003;
        var pkg = await SeedPackageAsync(tdb, credits: 8);
        var order = await SeedOrderAsync(tdb, OwnerType.User, ownerId, pkg.Id, code, OrderStatus.Paid);
        await SeedAccountAsync(tdb, OwnerType.User, ownerId, remaining: 8);

        var payos = new StubPayOs(PayOsPaymentStatus.Paid);
        var result = await NewService(tdb, payos, out _)
            .GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);

        Assert.Equal(OrderStatus.Paid, result!.Status);
        Assert.Equal(0, payos.CallCount);                                       // KHÔNG đối soát PayOS
        using var read = tdb.NewContext();
        Assert.Equal(8, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits); // không cộng thêm
        Assert.Equal(0, await read.CreditTransactions.CountAsync());
    }

    // (4a) Order id lạ → null (controller → 404).
    [Fact]
    public async Task Status_OrderIdLa_TraNull()
    {
        using var tdb = new PaymentTestDb();
        var payos = new StubPayOs(PayOsPaymentStatus.Paid);
        var result = await NewService(tdb, payos, out _)
            .GetOrderStatusAsync(Guid.NewGuid(), OwnerType.User, Guid.NewGuid());

        Assert.Null(result);
        Assert.Equal(0, payos.CallCount);
    }

    // (4b) Order của owner khác → null (404, không lộ đơn người khác).
    [Fact]
    public async Task Status_OwnerKhac_TraNull()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        const long code = 260711130004;
        var pkg = await SeedPackageAsync(tdb, credits: 4);
        var order = await SeedOrderAsync(tdb, OwnerType.User, ownerId, pkg.Id, code, OrderStatus.Pending);

        var payos = new StubPayOs(PayOsPaymentStatus.Paid);
        var result = await NewService(tdb, payos, out _)
            .GetOrderStatusAsync(order.Id, OwnerType.User, otherOwner);

        Assert.Null(result);                 // owner khác → null (không đối soát, không lộ)
        Assert.Equal(0, payos.CallCount);
    }

    // (5) Order Expired + PayOS trả Paid → KHÔNG auto-cộng credit (PAY-10) + KHÔNG gọi PayOS (terminal).
    [Fact]
    public async Task Status_Expired_PayOsPaid_KhongTuCongCredit()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        const long code = 260711130005;
        var pkg = await SeedPackageAsync(tdb, credits: 6);
        var order = await SeedOrderAsync(tdb, OwnerType.User, ownerId, pkg.Id, code, OrderStatus.Expired);
        await SeedAccountAsync(tdb, OwnerType.User, ownerId, remaining: 0);

        var payos = new StubPayOs(PayOsPaymentStatus.Paid);
        var result = await NewService(tdb, payos, out _)
            .GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);

        Assert.Equal(OrderStatus.Expired, result!.Status);   // giữ Expired
        Assert.Equal(0, payos.CallCount);                    // terminal → KHÔNG hỏi PayOS
        using var read = tdb.NewContext();
        Assert.Equal(0, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits); // KHÔNG cộng oan
        Assert.Equal(0, await read.CreditTransactions.CountAsync());
    }

    // (6) Pending + PayOS ≠ Paid → giữ Pending + lưu bằng chứng payment_transactions (append-only), KHÔNG cộng credit.
    [Fact]
    public async Task Status_Pending_PayOsChuaPaid_GiuPending_LuuBangChung()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        const long code = 260711130006;
        var pkg = await SeedPackageAsync(tdb, credits: 9);
        var order = await SeedOrderAsync(tdb, OwnerType.User, ownerId, pkg.Id, code, OrderStatus.Pending);
        await SeedAccountAsync(tdb, OwnerType.User, ownerId, remaining: 2);

        var payos = new StubPayOs(PayOsPaymentStatus.Processing, txnId: "FTX", raw: "{\"status\":\"PROCESSING\"}");
        var result = await NewService(tdb, payos, out _)
            .GetOrderStatusAsync(order.Id, OwnerType.User, ownerId);

        Assert.Equal(OrderStatus.Pending, result!.Status);   // chưa Paid → giữ Pending
        Assert.Null(result.PaidAt);
        Assert.Equal(1, payos.CallCount);

        using var read = tdb.NewContext();
        Assert.Equal(2, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits); // KHÔNG cộng
        Assert.Equal(0, await read.CreditTransactions.CountAsync());
        var evidence = await read.PaymentTransactions.Where(t => t.OrderId == order.Id).ToListAsync();
        Assert.Single(evidence);                              // bằng chứng append-only
        Assert.Equal("{\"status\":\"PROCESSING\"}", evidence[0].RawWebhookPayload);
        Assert.Equal("processing", evidence[0].Status);
    }
}
