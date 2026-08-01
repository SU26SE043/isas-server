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
    private static readonly Guid PlusPlanId = Guid.Parse("10000000-0000-0000-0000-000000000002");
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
    public async Task R9_Create_OneTimeCreditsBangKhong_Nem()
    {
        using var tdb = new PaymentTestDb();

        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb).CreatePackageAsync(new CreatePackageRequest
        {
            Name = "Gói lỗi",
            Type = PackageType.OneTime,
            PriceVnd = 10_000,
            InterviewCredits = 0,
        }, CancellationToken.None));
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

    [Fact]
    public async Task R9_Update_OneTimeCreditsBangKhong_NemVaGiuGiaTriCu()
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

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdatePackageAsync(created.Id,
            new UpdatePackageRequest { InterviewCredits = 0 }, CancellationToken.None));

        Assert.Equal(3, (await service.GetPackageAsync(created.Id, CancellationToken.None))!.InterviewCredits);
    }

    [Fact]
    public async Task R9_SubscriptionCreditsBangKhong_VanHopLe()
    {
        using var tdb = new PaymentTestDb();
        var service = NewService(tdb);
        var created = await service.CreatePackageAsync(new CreatePackageRequest
        {
            Name = "Gói tháng",
            Type = PackageType.Subscription,
            PriceVnd = 99_000,
            InterviewCredits = 0,
            DurationDays = 30,
            PlanId = PlusPlanId,
            Audience = PlanAudience.B2C,
        }, CancellationToken.None);

        var updated = await service.UpdatePackageAsync(created.Id,
            new UpdatePackageRequest { InterviewCredits = 0 }, CancellationToken.None);

        Assert.Equal(0, updated!.InterviewCredits);
    }

    [Fact]
    public async Task Subscription_RequiresPlanWithMatchingAudience()
    {
        using var tdb = new PaymentTestDb();
        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb).CreatePackageAsync(new CreatePackageRequest
        {
            Name = "Sai audience", Type = PackageType.Subscription, PriceVnd = 99_000, DurationDays = 30,
            PlanId = PlusPlanId, Audience = PlanAudience.B2B
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Update_SubscriptionBindingWithPendingOrder_IsRejected()
    {
        using var tdb = new PaymentTestDb();
        var (package, replacement) = await SeedSubscriptionPackagePairAsync(tdb);
        tdb.Db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = Guid.NewGuid(), PackageId = package.Id,
            Kind = OrderKind.SubscriptionPurchase, Status = OrderStatus.Pending, AmountVnd = 99_000,
            PayosOrderCode = 123456, ExpiredAt = DateTime.UtcNow.AddMinutes(30), CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb).UpdatePackageAsync(package.Id,
            new UpdatePackageRequest { PlanId = replacement.Id }, CancellationToken.None));
    }

    [Fact]
    public async Task Update_SubscriptionBindingWithActiveSubscription_IsRejected()
    {
        using var tdb = new PaymentTestDb();
        var (package, replacement) = await SeedSubscriptionPackagePairAsync(tdb);
        var owner = Guid.NewGuid();
        tdb.Db.AddRange(new CreditAccount
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active, UpdatedAt = DateTime.UtcNow
        }, new Subscription
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner, PackageId = package.Id,
            PlanId = package.PlanId, Audience = PlanAudience.B2C, TierCode = "first", TierRank = 1,
            InterviewFunding = InterviewFunding.Metered, MonthlyQuota = 30, EntitlementSnapshot = "{}", EntitlementHash = "x",
            StartedAt = DateTime.UtcNow, ActivatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30), CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => NewService(tdb).UpdatePackageAsync(package.Id,
            new UpdatePackageRequest { PlanId = replacement.Id }, CancellationToken.None));
    }

    private static async Task<(ProductPackage package, Plan replacement)> SeedSubscriptionPackagePairAsync(PaymentTestDb tdb)
    {
        var first = new Plan { Id = Guid.NewGuid(), Audience = PlanAudience.B2C, Code = "first", Name = "First", Rank = 1,
            InterviewFunding = InterviewFunding.Metered, MonthlyQuota = 30, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var replacement = new Plan { Id = Guid.NewGuid(), Audience = PlanAudience.B2C, Code = "replacement", Name = "Replacement", Rank = 2,
            InterviewFunding = InterviewFunding.Metered, MonthlyQuota = 30, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var package = new ProductPackage { Id = Guid.NewGuid(), Name = "Subscription", Type = PackageType.Subscription,
            PriceVnd = 99_000, DurationDays = 30, PlanId = first.Id, Audience = PlanAudience.B2C, IsActive = true, CreatedAt = DateTime.UtcNow };
        tdb.Db.AddRange(first, replacement, package); await tdb.Db.SaveChangesAsync();
        return (package, replacement);
    }
}
