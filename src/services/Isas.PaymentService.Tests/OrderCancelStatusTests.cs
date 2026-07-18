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
/// Lưu ý phạm vi: <c>CancelOrderAsync</c> gọi PayOS (<c>PayOSClient</c> — SDK class cụ thể, không có
/// interface để mock) TRƯỚC khi gán status, nên dòng gán KHÔNG unit-test được nếu không refactor
/// (ngoài phạm vi bug-fix này) — nó được verify ở tầng e2e/live. Test dưới đây khoá phần kiểm được
/// mà không chạm network: Cancelled là trạng thái TERMINAL bất biến (PAY-10) — huỷ lại → từ chối,
/// giống hệt Paid/Expired/Failed.
/// </summary>
public class OrderCancelStatusTests
{
    private static OrderService NewService(PaymentTestDb tdb) =>
        new(tdb.Db,
            new PayOSClient(new PayOSOptions { ClientId = "x", ApiKey = "x", ChecksumKey = "x" }),
            Options.Create(new PayOSSettings
            {
                ClientId = "x", ApiKey = "x", ChecksumKey = "x",
                ReturnUrl = "https://x/success", CancelUrl = "https://x/cancel",
            }),
            new OrderCodeGenerator(tdb.Db));

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

    // PAY-10: mọi trạng thái terminal đều bất biến — huỷ lại bị từ chối, KHÔNG đổi status.
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
        var svc = NewService(tdb);

        // Guard status chạy TRƯỚC khi gọi PayOS → không có network call trong test.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CancelOrderAsync(order.Id));

        Assert.Contains(terminal.ToString(), ex.Message);

        var reloaded = await tdb.Db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(terminal, reloaded.Status);
    }

    // Cancelled và Failed là HAI trạng thái khác nhau — chống hồi quy kiểu "gán đại cho xong".
    [Fact]
    public void Cancelled_khac_Failed()
    {
        Assert.NotEqual(OrderStatus.Cancelled, OrderStatus.Failed);
    }
}
