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

    // Seed an order at an explicit CreatedAt (+ optional Id) — for deterministic keyset paging tests (DB8).
    private static async Task<Order> SeedOrderAtAsync(
        PaymentTestDb tdb, OrderStatus status, long orderCode, DateTime createdAt, Guid? id = null)
    {
        var order = new Order
        {
            Id = id ?? Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = Guid.NewGuid(),
            Kind = OrderKind.CreditPack,
            PackageId = null,
            Status = status,
            AmountVnd = 100_000,
            PayosOrderCode = orderCode,
            ExpiredAt = createdAt.AddMinutes(30),
            PaidAt = status == OrderStatus.Paid ? createdAt : null,
            CreatedAt = createdAt
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

        var res = await NewService(tdb).ListAllOrdersAsync(null, null, null, null, null);

        Assert.Equal(3, res.Items.Count);
        Assert.Null(res.NextCursor);   // < default limit → last page (backward-compat: no cursor emitted)
        Assert.Contains(res.Items, o => o.OwnerType == OwnerType.User && o.OwnerId == userA);
        Assert.Contains(res.Items, o => o.OwnerType == OwnerType.Org && o.OwnerId == orgB);
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

        var paid = await NewService(tdb).ListAllOrdersAsync(OrderStatus.Paid, null, null, null, null);
        Assert.Equal(2, paid.Items.Count);
        Assert.All(paid.Items, o => Assert.Equal(OrderStatus.Paid, o.Status));

        var orgs = await NewService(tdb).ListAllOrdersAsync(null, OwnerType.Org, null, null, null);
        Assert.Single(orgs.Items);
        Assert.Equal(org, orgs.Items[0].OwnerId);

        var both = await NewService(tdb).ListAllOrdersAsync(OrderStatus.Paid, OwnerType.User, null, null, null);
        Assert.Single(both.Items);
        Assert.Equal(2002, both.Items[0].PayosOrderCode);
    }

    [Fact]
    public async Task ListAllOrders_Keyset_PagesWithoutOverlapOrGap()
    {
        using var tdb = new PaymentTestDb();
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        // 5 orders with distinct, increasing CreatedAt.
        for (var i = 0; i < 5; i++)
            await SeedOrderAtAsync(tdb, OrderStatus.Pending, 3000 + i, t0.AddMinutes(i));

        var seen = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await NewService(tdb).ListAllOrdersAsync(null, null, null, cursor, 2);
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(o => o.PayosOrderCode));
            cursor = page.NextCursor;
            pages++;
            Assert.True(pages <= 10, "paging did not terminate");
        } while (cursor is not null);

        // Newest-first order, every row exactly once (no gap, no overlap).
        Assert.Equal(new long[] { 3004, 3003, 3002, 3001, 3000 }, seen.ToArray());
    }

    [Fact]
    public async Task ListAllOrders_Keyset_TiebreakerOnIdenticalCreatedAt()
    {
        using var tdb = new PaymentTestDb();
        var same = new DateTime(2026, 7, 2, 9, 0, 0, DateTimeKind.Utc);
        await SeedOrderAtAsync(tdb, OrderStatus.Pending, 4001, same);
        await SeedOrderAtAsync(tdb, OrderStatus.Pending, 4002, same);
        await SeedOrderAtAsync(tdb, OrderStatus.Pending, 4003, same);

        var seen = new List<long>();
        string? cursor = null;
        for (var i = 0; i < 5 && (i == 0 || cursor is not null); i++)
        {
            var page = await NewService(tdb).ListAllOrdersAsync(null, null, null, cursor, 1);
            seen.AddRange(page.Items.Select(o => o.PayosOrderCode));
            cursor = page.NextCursor;
        }

        // Same CreatedAt across all three → Id tiebreaker must still walk each row exactly once.
        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task ListAllOrders_MalformedCursor_ReturnsFirstPage()
    {
        using var tdb = new PaymentTestDb();
        await SeedOrderAsync(tdb, OwnerType.User, Guid.NewGuid(), OrderStatus.Pending, 5001);
        await SeedOrderAsync(tdb, OwnerType.User, Guid.NewGuid(), OrderStatus.Pending, 5002);

        var page = await NewService(tdb).ListAllOrdersAsync(null, null, null, "not-a-valid-cursor", null);

        Assert.Equal(2, page.Items.Count);   // garbage cursor treated as first page, never throws
    }
}
