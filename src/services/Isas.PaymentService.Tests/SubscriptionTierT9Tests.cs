using System.Security.Cryptography;
using System.Text;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

public class SubscriptionTierT9Tests
{
    private static readonly PayOSSettings Payos = new()
    {
        ReturnUrl = "https://example.test/return", CancelUrl = "https://example.test/cancel"
    };

    private static ProductPackage Package(Guid planId, PlanAudience audience) => new()
    {
        Id = Guid.NewGuid(), Name = "Tier monthly", Type = PackageType.Subscription,
        PlanId = planId, Audience = audience, PriceVnd = 199_000, DurationDays = 30,
        IsActive = true, CreatedAt = DateTime.UtcNow
    };

    private static Plan Plan(Guid id, PlanAudience audience, string? code = null) => new()
    {
        Id = id, Audience = audience, Code = code ?? $"t9-{id:N}", Name = "T9 tier", Rank = 2,
        InterviewFunding = InterviewFunding.Metered, MonthlyQuota = 30,
        AdaptiveEnabled = true, AdaptiveMaxQuestions = 10, AdaptiveMaxFollowups = 3,
        GroundingEnabled = true, SelfConsistencyN = 2, CvAnalysisIncluded = true,
        RepoAnalysisIncluded = true, RoadmapEnabled = true,
        MaxActiveCampaigns = audience == PlanAudience.B2B ? 4 : null,
        MaxCandidatesCap = audience == PlanAudience.B2B ? 50 : null,
        SeatCount = audience == PlanAudience.B2B ? 5 : null,
        PostpaidEligible = audience == PlanAudience.B2B,
        EntitlementsJson = "[\"feature-x\"]", EntitlementsVersion = 7,
        IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static OrderService Orders(PaymentTestDb db) =>
        new(db.Db, null!, Options.Create(Payos), new FixedOrderCodes());

    [Theory]
    [InlineData(OwnerType.User, PlanAudience.B2B)]
    [InlineData(OwnerType.Org, PlanAudience.B2C)]
    public async Task CreateSubscriptionOrder_WrongAudience_RejectsBeforeOrderOrPayos(OwnerType owner, PlanAudience audience)
    {
        using var tdb = new PaymentTestDb();
        var plan = Plan(Guid.NewGuid(), audience);
        var package = Package(plan.Id, audience);
        tdb.Db.AddRange(plan, package);
        await tdb.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Orders(tdb).CreateOrderAsync(owner, Guid.NewGuid(),
            new OrderRequest.CreateOrderRequest { PackageId = package.Id }));

        Assert.Empty(await tdb.Db.Orders.ToListAsync());
    }

    [Fact]
    public async Task CreateSubscriptionOrder_B2CMatchingPlan_PersistsSubscriptionOrderBeforeGateway()
    {
        using var tdb = new PaymentTestDb();
        var plan = Plan(Guid.NewGuid(), PlanAudience.B2C);
        var package = Package(plan.Id, PlanAudience.B2C);
        tdb.Db.AddRange(plan, package);
        await tdb.Db.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => Orders(tdb).CreateOrderAsync(OwnerType.User, Guid.NewGuid(),
            new OrderRequest.CreateOrderRequest { PackageId = package.Id }));

        Assert.Equal(OrderKind.SubscriptionPurchase, Assert.Single(await tdb.Db.Orders.ToListAsync()).Kind);
    }

    [Fact]
    public async Task Webhook_Activation_StampsStablePlanSnapshotAndResolverDoesNotRetroact()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var plan = Plan(Guid.NewGuid(), PlanAudience.B2C);
        var package = Package(plan.Id, PlanAudience.B2C);
        var order = new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            Kind = OrderKind.SubscriptionPurchase, PackageId = package.Id, Status = OrderStatus.Pending,
            AmountVnd = package.PriceVnd, PayosOrderCode = 9001, ExpiredAt = DateTime.UtcNow.AddMinutes(30), CreatedAt = DateTime.UtcNow
        };
        tdb.Db.AddRange(plan, package, order);
        await tdb.Db.SaveChangesAsync();

        var webhook = new WebhookService(tdb.Db,
            new CreditAccountService(tdb.Db, null, Options.Create(new BillingSettings { FreeTrialCredits = 0 }), new SubscriptionService(tdb.Db)),
            null, new SubscriptionService(tdb.Db));
        Assert.Equal(WebhookApplyOutcome.SubscriptionActivated, await webhook.ApplyPaidWebhookAsync(9001, "t9", "{}"));

        var sub = Assert.Single(await tdb.Db.Subscriptions.AsNoTracking().ToListAsync());
        var originalCode = plan.Code;
        Assert.Equal(plan.Id, sub.PlanId);
        Assert.Equal(originalCode, sub.TierCode);
        Assert.Equal(2, sub.TierRank);
        Assert.Equal(30, sub.MonthlyQuota);
        Assert.Contains("\"adaptiveEnabled\":true", sub.EntitlementSnapshot);
        Assert.Equal(Sha256(sub.EntitlementSnapshot), sub.EntitlementHash);

        plan.Code = $"changed-{plan.Id:N}";
        plan.MonthlyQuota = 999;
        plan.EntitlementsJson = "[\"changed\"]";
        await tdb.Db.SaveChangesAsync();
        var resolved = await new EntitlementResolver(tdb.NewContext()).ResolveAsync(OwnerType.User, owner);
        Assert.Equal(originalCode, resolved.TierCode);
        Assert.Equal(30, resolved.MonthlyQuota);
        Assert.Equal(sub.EntitlementSnapshot, resolved.EntitlementSnapshot);
        Assert.NotEqual(sub.EntitlementHash, Sha256(sub.EntitlementSnapshot + "corruption"));
    }

    [Fact]
    public async Task Webhook_PackagePlanAudienceMismatch_LeavesPaidOrderWithoutSubscription()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var plan = Plan(Guid.NewGuid(), PlanAudience.B2B);
        var package = Package(plan.Id, PlanAudience.B2C);
        var order = new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            Kind = OrderKind.SubscriptionPurchase, PackageId = package.Id, Status = OrderStatus.Pending,
            AmountVnd = package.PriceVnd, PayosOrderCode = 9002, ExpiredAt = DateTime.UtcNow.AddMinutes(30), CreatedAt = DateTime.UtcNow
        };
        tdb.Db.AddRange(plan, package, order);
        await tdb.Db.SaveChangesAsync();

        var webhook = new WebhookService(tdb.Db,
            new CreditAccountService(tdb.Db, null, Options.Create(new BillingSettings { FreeTrialCredits = 0 }), new SubscriptionService(tdb.Db)),
            null, new SubscriptionService(tdb.Db));
        await webhook.ApplyPaidWebhookAsync(9002, "t9", "{}");

        Assert.Equal(OrderStatus.Paid, (await tdb.NewContext().Orders.SingleAsync()).Status);
        Assert.Empty(await tdb.NewContext().Subscriptions.ToListAsync());
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FixedOrderCodes : IOrderCodeGenerator
    {
        public Task<long> GenerateAsync(CancellationToken ct = default) => Task.FromResult(9000L);
    }
}
