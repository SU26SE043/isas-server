using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// B2 — <c>POST/PUT /package</c> trước đây nhận <c>name:""</c> và <c>priceVnd:-5</c> rồi trả 200:
/// <c>Validate</c> chỉ kiểm type + credits/days có mặt, còn <c>UpdatePackageAsync</c> không validate gì.
/// Nay <c>ValidateSanity</c> chung cho cả hai đường: name rỗng / số âm → <see cref="ArgumentException"/>
/// (controller đã bắt → 400). Field nullable ⇒ chỉ kiểm khi CÓ MẶT (Update là partial).
/// </summary>
public class PackageValidationTests
{
    private static PackageService NewService(PaymentTestDb tdb) =>
        new(NullLogger<PackageService>.Instance, tdb.Db);

    // ── Create ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_NameRong_Nem()
    {
        using var tdb = new PaymentTestDb();
        var service = NewService(tdb);
        var request = new CreatePackageRequest
        {
            Name = "   ",
            Type = PackageType.OneTime,
            PriceVnd = 10_000,
            InterviewCredits = 5,
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreatePackageAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Create_PriceVndAm_Nem()
    {
        using var tdb = new PaymentTestDb();
        var service = NewService(tdb);
        var request = new CreatePackageRequest
        {
            Name = "Gói hợp lệ",
            Type = PackageType.OneTime,
            PriceVnd = -5,
            InterviewCredits = 5,
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreatePackageAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Create_HopLe_TraPackageResponse()
    {
        using var tdb = new PaymentTestDb();
        var service = NewService(tdb);
        var request = new CreatePackageRequest
        {
            Name = "Gói 5 lượt",
            Type = PackageType.OneTime,
            PriceVnd = 99_000,
            InterviewCredits = 5,
        };

        var result = await service.CreatePackageAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Gói 5 lượt", result.Name);
        Assert.Equal(99_000, result.PriceVnd);
        Assert.Equal(5, result.InterviewCredits);
        Assert.True(result.IsActive);
    }

    // ── Update ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_PriceVndAm_Nem()
    {
        using var tdb = new PaymentTestDb();
        var service = NewService(tdb);
        var created = await service.CreatePackageAsync(new CreatePackageRequest
        {
            Name = "Gói gốc",
            Type = PackageType.OneTime,
            PriceVnd = 50_000,
            InterviewCredits = 3,
        }, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdatePackageAsync(created.Id,
                new UpdatePackageRequest { PriceVnd = -1 }, CancellationToken.None));
    }
}
