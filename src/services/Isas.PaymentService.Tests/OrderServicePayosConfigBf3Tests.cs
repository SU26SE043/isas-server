using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PayOS;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Tests;

/// <summary>
/// BF3 — PayOS ReturnUrl/CancelUrl chưa cấu hình → CreateOrderAsync ném PaymentGatewayException
/// (controller map 502), KHÔNG persist order mồ côi. Bug bắt ở API sweep layer-3: server thiếu env
/// PayOS__ReturnUrl/CancelUrl → PayOS reject "return_url null" → 500 stack thô. Guard fail sớm trước
/// khi gọi PayOS nên test dùng PayOSClient dummy (không có network call).
/// </summary>
public class OrderServicePayosConfigBf3Tests
{
    private static OrderService NewService(PaymentTestDb tdb, PayOSSettings settings) =>
        new(tdb.Db,
            new PayOSClient(new PayOSOptions { ClientId = "x", ApiKey = "x", ChecksumKey = "x" }),
            Options.Create(settings),
            new OrderCodeGenerator(tdb.Db));

    private static async Task<ProductPackage> SeedActivePackageAsync(PaymentTestDb tdb)
    {
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = "Sandbox 1 credit",
            Type = PackageType.OneTime,
            PriceVnd = 2000,
            InterviewCredits = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        tdb.Db.ProductPackages.Add(pkg);
        await tdb.Db.SaveChangesAsync();
        return pkg;
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, null)]
    [InlineData("https://x/success", "")]      // chỉ thiếu cancel
    [InlineData("", "https://x/cancel")]        // chỉ thiếu return
    public async Task CreateOrder_missing_payos_urls_throws_gateway_and_creates_no_orphan_order(
        string? returnUrl, string? cancelUrl)
    {
        using var tdb = new PaymentTestDb();
        var pkg = await SeedActivePackageAsync(tdb);
        var svc = NewService(tdb, new PayOSSettings
        {
            ClientId = "x", ApiKey = "x", ChecksumKey = "x",
            ReturnUrl = returnUrl!, CancelUrl = cancelUrl!,
        });

        await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            svc.CreateOrderAsync(OwnerType.User, Guid.NewGuid(),
                new CreateOrderRequest { PackageId = pkg.Id }));

        // Fail sớm trước persist → không order mồ côi.
        Assert.Empty(tdb.NewContext().Orders);
    }

    [Fact]
    public async Task CreateOrder_unknown_package_still_404_before_payos_guard()
    {
        using var tdb = new PaymentTestDb();
        var svc = NewService(tdb, new PayOSSettings
        {
            ClientId = "x", ApiKey = "x", ChecksumKey = "x",
            ReturnUrl = "", CancelUrl = "",   // config hỏng nhưng package-not-found phải bắt trước
        });

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.CreateOrderAsync(OwnerType.User, Guid.NewGuid(),
                new CreateOrderRequest { PackageId = Guid.NewGuid() }));
    }
}
