using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PayOS;

namespace Isas.PaymentService.Tests;

/// <summary>
/// AUTH-7 — PlatformAdmin oversight (read-only, cross-owner). ListAllOrders trả đơn của MỌI chủ ví
/// (không lọc owner), optional lọc status/ownerType. Service-level: chỉ đụng _db.Orders → PayOSClient
/// dummy (không network, mẫu OrderServicePayosConfigBf3Tests). Seed mẫu OrderStatusServiceTests.
/// </summary>
public class AdminOrdersOversightTests
{
    private static OrderService NewService(PaymentTestDb tdb) =>
        new(tdb.Db,
            new PayOSClient(new PayOSOptions { ClientId = "x", ApiKey = "x", ChecksumKey = "x" }),
            Options.Create(new PayOSSettings()),
            new OrderCodeGenerator(tdb.Db));

    private static async Task<Order> SeedOrderAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, OrderStatus status, long orderCode)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            Kind = OrderKind.CreditPack,
            PackageId = null,   // tránh FK ProductPackages (oversight test không cần package thật)
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

    [Fact]
    public async Task ListAllOrders_ReturnsOrdersAcrossOwners()
    {
        using var tdb = new PaymentTestDb();
        var userA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await SeedOrderAsync(tdb, OwnerType.User, userA, OrderStatus.Pending, 1001);
        await SeedOrderAsync(tdb, OwnerType.User, userA, OrderStatus.Paid, 1002);
        await SeedOrderAsync(tdb, OwnerType.Org, orgB, OrderStatus.Paid, 1003);

        var res = await NewService(tdb).ListAllOrdersAsync(null, null);

        Assert.Equal(3, res.Count);
        Assert.Contains(res, o => o.OwnerType == OwnerType.User && o.OwnerId == userA);
        Assert.Contains(res, o => o.OwnerType == OwnerType.Org && o.OwnerId == orgB);
    }

    [Fact]
    public async Task ListAllOrders_FilterByStatusAndOwnerType()
    {
        using var tdb = new PaymentTestDb();
        var user = Guid.NewGuid();
        var org = Guid.NewGuid();
        await SeedOrderAsync(tdb, OwnerType.User, user, OrderStatus.Pending, 2001);
        await SeedOrderAsync(tdb, OwnerType.User, user, OrderStatus.Paid, 2002);
        await SeedOrderAsync(tdb, OwnerType.Org, org, OrderStatus.Paid, 2003);

        var paid = await NewService(tdb).ListAllOrdersAsync(OrderStatus.Paid, null);
        Assert.Equal(2, paid.Count);
        Assert.All(paid, o => Assert.Equal(OrderStatus.Paid, o.Status));

        var orgs = await NewService(tdb).ListAllOrdersAsync(null, OwnerType.Org);
        Assert.Single(orgs);
        Assert.Equal(org, orgs[0].OwnerId);

        var both = await NewService(tdb).ListAllOrdersAsync(OrderStatus.Paid, OwnerType.User);
        Assert.Single(both);
        Assert.Equal(2002, both[0].PayosOrderCode);
    }
}
