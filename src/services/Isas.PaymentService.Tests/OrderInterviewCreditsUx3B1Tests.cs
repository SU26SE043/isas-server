using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PayOS;

namespace Isas.PaymentService.Tests;

/// <summary>
/// UX3-B1 — <c>OrderResponse.InterviewCredits</c>: biên lai FE đọc <c>order.interviewCredits</c> để
/// hiện dòng "số lượt phỏng vấn"; trước bản này response không có trường đó nên dòng luôn trống.
///
/// Giá trị lấy từ <c>Order.Package?.InterviewCredits</c>. NULLABLE có chủ đích: đơn tất toán hoá đơn
/// (<see cref="OrderKind.InvoiceSettlement"/>) không gắn package ⇒ <c>null</c> ("không mua lượt nào"),
/// KHÁC hẳn gói 0 lượt (0).
///
/// Seed và query dùng DbContext khác nhau (mẫu <see cref="OrderPackageNameTests"/>): EF relationship
/// fixup trong cùng context có thể điền navigation Package dù production query thiếu Include → test
/// xanh giả.
/// </summary>
public class OrderInterviewCreditsUx3B1Tests
{
    private static OrderService NewService(PaymentTestDb tdb) =>
        new(tdb.NewContext(),
            new PayOSClient(new PayOSOptions { ClientId = "x", ApiKey = "x", ChecksumKey = "x" }),
            Options.Create(new PayOSSettings()),
            new OrderCodeGenerator(tdb.Db));

    private const long PackPriceVnd = 149_000;
    private const int PackCredits = 10;

    private static async Task<(Guid OwnerId, Order Order)> SeedCreditPackOrderAsync(PaymentTestDb tdb)
    {
        var now = DateTime.UtcNow;
        var package = new ProductPackage
        {
            Id = Guid.NewGuid(), Name = "Gói luyện phỏng vấn 10 lượt", Type = PackageType.OneTime,
            PriceVnd = PackPriceVnd, InterviewCredits = PackCredits, IsActive = true, CreatedAt = now
        };
        var order = new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = Guid.NewGuid(),
            Kind = OrderKind.CreditPack, PackageId = package.Id, Status = OrderStatus.Paid,
            AmountVnd = PackPriceVnd, PayosOrderCode = 8100, ExpiredAt = now.AddMinutes(30),
            PaidAt = now, CreatedAt = now
        };
        tdb.Db.AddRange(package, order);
        await tdb.Db.SaveChangesAsync();
        return (order.OwnerId, order);
    }

    private static async Task<Order> SeedInvoiceSettlementOrderAsync(PaymentTestDb tdb)
    {
        var now = DateTime.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.Org, OwnerId = Guid.NewGuid(),
            Kind = OrderKind.InvoiceSettlement, PackageId = null, Status = OrderStatus.Paid,
            AmountVnd = 2_000_000, PayosOrderCode = 8101, ExpiredAt = now.AddMinutes(30),
            PaidAt = now, CreatedAt = now
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task MyOrders_DonCreditPack_TraSoLuotCuaGoi()
    {
        using var tdb = new PaymentTestDb();
        var (ownerId, _) = await SeedCreditPackOrderAsync(tdb);

        var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, ownerId, null, null, null);

        Assert.Equal(PackCredits, Assert.Single(page.Items).InterviewCredits);
    }

    [Fact]
    public async Task ChiTietDon_DonCreditPack_TraSoLuotCuaGoi()
    {
        using var tdb = new PaymentTestDb();
        var (_, order) = await SeedCreditPackOrderAsync(tdb);

        var result = await NewService(tdb).GetOrderAsync(order.Id);

        Assert.NotNull(result);
        Assert.Equal(PackCredits, result.InterviewCredits);
    }

    [Fact]
    public async Task DonTatToanHoaDon_KhongCoGoi_TraNull_KhongPhai0()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedInvoiceSettlementOrderAsync(tdb);

        var result = await NewService(tdb).GetOrderAsync(order.Id);

        Assert.NotNull(result);
        // null = "đơn này không mua lượt nào"; 0 = "gói 0 lượt" — hai nghĩa khác nhau, không được lẫn.
        Assert.Null(result.InterviewCredits);
    }

    [Fact]
    public async Task AmountVnd_KhongDoi_ChongHoiQuy()
    {
        using var tdb = new PaymentTestDb();
        var (ownerId, order) = await SeedCreditPackOrderAsync(tdb);
        var svc = NewService(tdb);

        var detail = await svc.GetOrderAsync(order.Id);
        var listItem = Assert.Single(
            (await svc.GetOwnerOrdersAsync(OwnerType.User, ownerId, null, null, null)).Items);

        Assert.NotNull(detail);
        Assert.Equal(PackPriceVnd, detail.AmountVnd);
        Assert.Equal(PackPriceVnd, listItem.AmountVnd);
    }
}
