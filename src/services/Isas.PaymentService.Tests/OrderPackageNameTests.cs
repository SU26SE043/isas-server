using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PayOS;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Hợp đồng packageName trên các đường đọc order.
///
/// Seed và query phải dùng DbContext khác nhau: EF relationship fixup trong cùng context có thể điền
/// navigation Package dù production query thiếu Include, khiến test xanh giả.
/// </summary>
public class OrderPackageNameTests
{
    private static OrderService NewService(PaymentTestDb tdb) =>
            new(tdb.NewContext(),
            new PayOSClient(new PayOSOptions { ClientId = "x", ApiKey = "x", ChecksumKey = "x" }),
            Options.Create(new PayOSSettings()),
            new OrderCodeGenerator(tdb.Db));

    private static async Task<(Guid OwnerId, ProductPackage Package, Order Order)> SeedPackageOrderAsync(
        PaymentTestDb tdb)
    {
        var now = DateTime.UtcNow;
        var package = new ProductPackage
        {
            Id = Guid.NewGuid(), Name = "Gói luyện phỏng vấn 10 lượt", Type = PackageType.OneTime,
            PriceVnd = 100_000, InterviewCredits = 10, IsActive = true, CreatedAt = now
        };
        var order = new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = Guid.NewGuid(),
            Kind = OrderKind.CreditPack, PackageId = package.Id, Status = OrderStatus.Paid,
            AmountVnd = package.PriceVnd, PayosOrderCode = 7000, ExpiredAt = now.AddMinutes(30),
            PaidAt = now, CreatedAt = now
        };
        tdb.Db.AddRange(package, order);
        await tdb.Db.SaveChangesAsync();
        return (order.OwnerId, package, order);
    }

    [Fact]
    public async Task MyOrders_CoPackage_TraVeTenPackage()
    {
        using var tdb = new PaymentTestDb();
        var (ownerId, package, _) = await SeedPackageOrderAsync(tdb);

        var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, ownerId, null, null, null);

        Assert.Equal(package.Name, Assert.Single(page.Items).PackageName);
    }

    [Fact]
    public async Task ChiTietDon_CoPackage_TraVeTenPackage()
    {
        using var tdb = new PaymentTestDb();
        var (_, package, order) = await SeedPackageOrderAsync(tdb);

        var result = await NewService(tdb).GetOrderAsync(order.Id);

        Assert.NotNull(result);
        Assert.Equal(package.Name, result.PackageName);
    }

    [Fact]
    public async Task DonTatToanHoaDon_KhongCoPackage_TraVeNull()
    {
        using var tdb = new PaymentTestDb();
        var order = new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.Org, OwnerId = Guid.NewGuid(),
            Kind = OrderKind.InvoiceSettlement, Status = OrderStatus.Paid, AmountVnd = 100_000,
            PayosOrderCode = 7001, ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            PaidAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();

        var result = await NewService(tdb).GetOrderAsync(order.Id);

        Assert.NotNull(result);
        Assert.Null(result.PackageName);
    }
}
