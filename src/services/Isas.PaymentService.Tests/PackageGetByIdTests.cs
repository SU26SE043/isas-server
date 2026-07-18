using Isas.PaymentService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Bug bắt ở e2e 2026-07-18: <c>GET /payment/package/{id}</c> với id không tồn tại trả **500**
/// (<see cref="NullReferenceException"/> trong <c>PackageResponse.ToResponse(null)</c>) thay vì 404 —
/// endpoint này <c>[AllowAnonymous]</c> nên ai cũng bắn được id rác vào để làm service văng.
///
/// Signature đã là <c>Task&lt;PackageResponse?&gt;</c> ⇒ ý định thiết kế vốn là "không thấy → null → 404",
/// chỉ thiếu null-check (khác <c>UpdatePackageAsync</c> vốn đã check đúng).
///
/// Kèm theo, siết luôn contract: payment.md:109 mô tả endpoint là gói **"đang bán"**, và GET catalog
/// lọc <c>IsActive</c> — nhưng GET-by-id thì không, nên gói đã soft-delete vẫn xem được public.
/// Nay lọc <c>IsActive</c> cho khớp: id lạ và gói đã ngừng bán đều → null → 404 (không lộ gói đã rút).
/// </summary>
public class PackageGetByIdTests
{
    private static async Task<ProductPackage> SeedPackageAsync(PaymentTestDb tdb, bool isActive)
    {
        var package = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = isActive ? "Gói đang bán" : "Gói đã rút",
            Type = PackageType.OneTime,
            PriceVnd = 2000,
            InterviewCredits = 5,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
        };
        tdb.Db.ProductPackages.Add(package);
        await tdb.Db.SaveChangesAsync();
        return package;
    }

    // Chính bug: id không tồn tại phải ra null (→404), KHÔNG được ném NullReferenceException (→500).
    [Fact]
    public async Task GetPackage_IdKhongTonTai_TraNull_KhongNem()
    {
        using var tdb = new PaymentTestDb();
        var service = new PackageService(NullLogger<PackageService>.Instance, tdb.Db);

        var result = await service.GetPackageAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    // Gói đã ngừng bán (soft-delete) không còn xem được qua endpoint public → khớp GET catalog.
    [Fact]
    public async Task GetPackage_GoiNgungBan_TraNull()
    {
        using var tdb = new PaymentTestDb();
        var inactive = await SeedPackageAsync(tdb, isActive: false);
        var service = new PackageService(NullLogger<PackageService>.Instance, tdb.Db);

        var result = await service.GetPackageAsync(inactive.Id, CancellationToken.None);

        Assert.Null(result);
    }

    // Không-regression: gói đang bán vẫn trả đủ dữ liệu như trước.
    [Fact]
    public async Task GetPackage_GoiDangBan_TraDuDuLieu()
    {
        using var tdb = new PaymentTestDb();
        var active = await SeedPackageAsync(tdb, isActive: true);
        var service = new PackageService(NullLogger<PackageService>.Instance, tdb.Db);

        var result = await service.GetPackageAsync(active.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(active.Id, result!.Id);
        Assert.Equal(active.Name, result.Name);
        Assert.Equal(active.PriceVnd, result.PriceVnd);
        Assert.True(result.IsActive);
    }
}
