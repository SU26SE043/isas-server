using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// DB3 — product_packages.price_vnd là bigint (ProductPackage.PriceVnd = long). int cũ (~2,1 tỷ ₫)
/// tràn thầm lặng với gói giá lớn. Kiểm giá trị VND vượt trần int round-trip qua DB không mất/tràn.
/// </summary>
public class ProductPackagePriceLongTests
{
    [Fact]
    public async Task ProductPackage_PriceVnd_StoresAndReadsValueBeyondIntMax()
    {
        using var t = new PaymentTestDb();

        const long big = 3_000_000_000L;   // > int.MaxValue (2_147_483_647)
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = "Enterprise annual",
            Type = PackageType.OneTime,
            PriceVnd = big,
            InterviewCredits = 1000,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.ProductPackages.Add(pkg);
        await t.Db.SaveChangesAsync();

        // Đọc lại qua context riêng → chắc chắn persist đúng (không tràn int).
        await using var read = t.NewContext();
        var saved = await read.ProductPackages.AsNoTracking().FirstAsync(p => p.Id == pkg.Id);
        Assert.Equal(big, saved.PriceVnd);
    }
}
