using Isas.PaymentService.Services;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// P7 — order_code time+random, unique + retry. Verify của task:
// sinh 10k code không trùng; mọi code dương và ≤ 9.007.199.254.740.991 (2^53-1, trần PayOS — D12).
public class OrderCodeGeneratorTests
{
    private static async Task<ProductPackage> SeedPackageAsync(PaymentDbContext db)
    {
        var package = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = "Test Pack",
            Type = PackageType.OneTime,
            PriceVnd = 10_000,
            InterviewCredits = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.ProductPackages.Add(package);
        await db.SaveChangesAsync();
        return package;
    }

    private static Order NewOrder(Guid packageId, long orderCode) => new()
    {
        Id = Guid.NewGuid(),
        OwnerType = OwnerType.User,
        OwnerId = Guid.NewGuid(),
        Kind = OrderKind.CreditPack,
        PackageId = packageId,
        Status = OrderStatus.Pending,
        AmountVnd = 10_000,
        PayosOrderCode = orderCode,
        ExpiredAt = DateTime.UtcNow.AddMinutes(30),
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task GenerateAsync_10000Codes_AllUniquePositiveWithinCeiling()
    {
        using var tdb = new PaymentTestDb();
        var package = await SeedPackageAsync(tdb.Db);
        var gen = new OrderCodeGenerator(tdb.Db);

        var seen = new HashSet<long>();

        for (var i = 0; i < 10_000; i++)
        {
            var code = await gen.GenerateAsync();

            Assert.True(code > 0, $"order_code phải là số nguyên dương: {code}");
            Assert.True(code <= OrderCodeGenerator.Ceiling, $"order_code vượt trần PayOS (2^53-1): {code}");
            Assert.True(seen.Add(code), $"order_code sinh trùng ở lần thứ {i}: {code}");

            // Persist ngay để lần sinh kế tiếp check trùng thật với DB (đúng đường chạy production).
            tdb.Db.Orders.Add(NewOrder(package.Id, code));
            await tdb.Db.SaveChangesAsync();
        }

        Assert.Equal(10_000, seen.Count);
    }

    [Fact]
    public async Task GenerateAsync_DbCollision_RegeneratesUntilUnique()
    {
        using var tdb = new PaymentTestDb();
        var package = await SeedPackageAsync(tdb.Db);

        const long collidingCode = 260711999001L;
        const long freeCode = 260711999002L;

        // Order đã tồn tại sẵn với mã collidingCode → ép candidate đầu tiên đụng UNIQUE.
        tdb.Db.Orders.Add(NewOrder(package.Id, collidingCode));
        await tdb.Db.SaveChangesAsync();

        var candidates = new Queue<long>([collidingCode, freeCode]);
        var attempts = new List<long>();

        long CandidateFactory()
        {
            var candidate = candidates.Count > 0 ? candidates.Dequeue() : freeCode;
            attempts.Add(candidate);
            return candidate;
        }

        var gen = new OrderCodeGenerator(tdb.Db, CandidateFactory);
        var result = await gen.GenerateAsync();

        Assert.Equal(freeCode, result);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(collidingCode, attempts[0]);
        Assert.Equal(freeCode, attempts[1]);
    }

    [Fact]
    public async Task GenerateAsync_ExhaustsRetries_ThrowsInvalidOperationException()
    {
        using var tdb = new PaymentTestDb();
        var package = await SeedPackageAsync(tdb.Db);

        const long alwaysCollidingCode = 260711999003L;
        tdb.Db.Orders.Add(NewOrder(package.Id, alwaysCollidingCode));
        await tdb.Db.SaveChangesAsync();

        // Factory luôn trả về mã đã tồn tại → hết lượt retry (bounded) phải ném lỗi rõ ràng,
        // không lặp vô hạn.
        var gen = new OrderCodeGenerator(tdb.Db, () => alwaysCollidingCode);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gen.GenerateAsync());
    }
}
