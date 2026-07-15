using Microsoft.EntityFrameworkCore;
using PaymentService.Models;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Tests;

/// <summary>
/// COMMIT-5 — orders.amount_vnd là bigint (Order.AmountVnd = long, payment.md §DB). int cũ (~2,1 tỷ ₫)
/// tràn thầm lặng với pack lớn / hóa đơn postpaid gộp kỳ. Kiểm giá trị VND vượt trần int round-trip
/// qua DB + qua DTO OrderResponse mà không mất/tràn.
/// </summary>
public class OrderAmountLongTests
{
    [Fact]
    public async Task Order_AmountVnd_StoresAndReadsValueBeyondIntMax()
    {
        using var t = new PaymentTestDb();

        const long big = 3_000_000_000L;   // > int.MaxValue (2_147_483_647)
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = Guid.NewGuid(),
            Kind = OrderKind.CreditPack,
            Status = OrderStatus.Pending,
            AmountVnd = big,
            PayosOrderCode = 250715_0001L,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = DateTime.UtcNow
        };
        t.Db.Orders.Add(order);
        await t.Db.SaveChangesAsync();

        // Đọc lại qua context riêng → chắc chắn giá trị persist đúng (không tràn int).
        await using var read = t.NewContext();
        var saved = await read.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(big, saved.AmountVnd);

        // DTO trả FE cũng mang long — không tràn khi format response.
        var resp = OrderResponse.ToResponse(saved);
        Assert.Equal(big, resp.AmountVnd);
    }
}
