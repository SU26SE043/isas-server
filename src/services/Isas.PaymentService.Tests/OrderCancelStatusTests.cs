using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PayOS;

namespace Isas.PaymentService.Tests;

/// <summary>
/// PAY-10 — user chủ động huỷ đơn phải ra <see cref="OrderStatus.Cancelled"/>, KHÔNG phải
/// <see cref="OrderStatus.Failed"/> (= cổng thanh toán lỗi). Bug bắt ở e2e 2026-07-18: cancel ghi
/// Failed ⇒ 4 trạng thái terminal sập còn 3, đối soát không phân biệt được "user tự huỷ" với
/// "thanh toán hỏng".
///
/// Đường huỷ nay đi qua <see cref="IPayOsCancelClient"/> (stub được) nên cả hai vế test được không chạm
/// network: guard terminal (PAY-10) + đường vui + PayOS từ chối + ĐUA với webhook Paid (guard atomic
/// WHERE status=Pending — bản cũ FindAsync+SaveChanges ghi đè Paid thành Cancelled).
/// </summary>
public class OrderCancelStatusTests
{
    private static OrderService NewService(PaymentTestDb tdb, IPayOsCancelClient? cancel = null) =>
        new(tdb.Db,
            new PayOSClient(new PayOSOptions { ClientId = "x", ApiKey = "x", ChecksumKey = "x" }),
            Options.Create(new PayOSSettings
            {
                ClientId = "x", ApiKey = "x", ChecksumKey = "x",
                ReturnUrl = "https://x/success", CancelUrl = "https://x/cancel",
            }),
            new OrderCodeGenerator(tdb.Db),
            cancel ?? new StubCancel());

    /// <summary>Stub huỷ link PayOS: đếm lượt gọi, tuỳ chọn ném/side-effect (giả lập webhook Paid chen ngang).</summary>
    private sealed class StubCancel : IPayOsCancelClient
    {
        public int CallCount { get; private set; }
        public long? LastOrderCode { get; private set; }
        public Func<Task>? SideEffect { get; init; }
        public bool Throw { get; init; }

        public async Task CancelPaymentLinkAsync(long orderCode, string reason, CancellationToken ct = default)
        {
            CallCount++;
            LastOrderCode = orderCode;
            if (SideEffect is not null) await SideEffect();
            if (Throw) throw new PaymentGatewayException("PayOS từ chối huỷ (stub)");
        }
    }

    private static async Task<Order> SeedOrderAsync(PaymentTestDb tdb, OrderStatus status)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = Guid.NewGuid(),
            Kind = OrderKind.CreditPack,
            Status = status,
            AmountVnd = 2000,
            PayosOrderCode = 2607181200000001,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = DateTime.UtcNow,
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    // PAY-10: mọi trạng thái terminal đều bất biến — huỷ lại bị từ chối, KHÔNG đổi status, KHÔNG gọi PayOS.
    // Cancelled nằm trong danh sách này chính là điều bug cũ làm mất (cancel ghi Failed).
    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Failed)]
    public async Task CancelOrder_tren_don_terminal_bi_tu_choi_va_giu_nguyen_status(OrderStatus terminal)
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedOrderAsync(tdb, terminal);
        var cancel = new StubCancel();
        var svc = NewService(tdb, cancel);

        // Guard status chạy TRƯỚC khi gọi PayOS → không có network call.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CancelOrderAsync(order.Id));

        Assert.Contains(terminal.ToString(), ex.Message);
        Assert.Equal(0, cancel.CallCount);

        var reloaded = await tdb.Db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(terminal, reloaded.Status);
    }

    // Đường vui: Pending → huỷ link PayOS (đúng orderCode) → Cancelled + updated_at đóng dấu.
    [Fact]
    public async Task CancelOrder_Pending_HuyLinkPayOs_RoiCancelled()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedOrderAsync(tdb, OrderStatus.Pending);
        var cancel = new StubCancel();

        await NewService(tdb, cancel).CancelOrderAsync(order.Id);

        Assert.Equal(1, cancel.CallCount);
        Assert.Equal(order.PayosOrderCode, cancel.LastOrderCode);
        var reloaded = await tdb.Db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Cancelled, reloaded.Status);
        Assert.Null(reloaded.PaidAt);
    }

    // PayOS từ chối huỷ mà đơn vẫn Pending → PaymentGatewayException (controller map 502), đơn GIỮ Pending.
    [Fact]
    public async Task CancelOrder_PayOsTuChoi_DonVanPending_Nem502_GiuPending()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedOrderAsync(tdb, OrderStatus.Pending);

        await Assert.ThrowsAsync<PaymentGatewayException>(
            () => NewService(tdb, new StubCancel { Throw = true }).CancelOrderAsync(order.Id));

        var reloaded = await tdb.Db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Pending, reloaded.Status);
    }

    // ĐUA: webhook Paid chốt đơn TRONG LÚC đang gọi PayOS huỷ, PayOS từ chối (link đã Paid) → 400 nêu đúng
    // trạng thái Paid (không phải 502 đổ cho cổng), và đơn KHÔNG bị ghi đè thành Cancelled.
    [Fact]
    public async Task CancelOrder_WebhookPaidChenNgang_PayOsTuChoi_Bao400Paid_KhongGhiDe()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedOrderAsync(tdb, OrderStatus.Pending);
        var cancel = new StubCancel
        {
            Throw = true,
            SideEffect = async () =>
            {
                using var other = tdb.NewContext();
                await other.Orders.Where(o => o.Id == order.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Paid)
                                              .SetProperty(o => o.PaidAt, DateTime.UtcNow));
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(tdb, cancel).CancelOrderAsync(order.Id));

        Assert.Contains("Paid", ex.Message);
        var reloaded = await tdb.Db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Paid, reloaded.Status);
        Assert.NotNull(reloaded.PaidAt);
    }

    // ĐUA (biến thể PayOS huỷ THÀNH CÔNG nhưng webhook Paid vẫn tới trước bước ghi) → guard WHERE Pending
    // 0 row → ném 400 Paid, KHÔNG ghi đè. Bản cũ (FindAsync + SaveChanges) sẽ đè Paid thành Cancelled.
    [Fact]
    public async Task CancelOrder_WebhookPaidChenNgangSauKhiPayOsHuy_GuardAtomic_KhongGhiDe()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedOrderAsync(tdb, OrderStatus.Pending);
        var cancel = new StubCancel
        {
            SideEffect = async () =>
            {
                using var other = tdb.NewContext();
                await other.Orders.Where(o => o.Id == order.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Paid)
                                              .SetProperty(o => o.PaidAt, DateTime.UtcNow));
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(tdb, cancel).CancelOrderAsync(order.Id));

        Assert.Contains("Paid", ex.Message);
        var reloaded = await tdb.Db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Paid, reloaded.Status);
    }

    // Cancelled và Failed là HAI trạng thái khác nhau — chống hồi quy kiểu "gán đại cho xong".
    [Fact]
    public void Cancelled_khac_Failed()
    {
        Assert.NotEqual(OrderStatus.Cancelled, OrderStatus.Failed);
    }
}
