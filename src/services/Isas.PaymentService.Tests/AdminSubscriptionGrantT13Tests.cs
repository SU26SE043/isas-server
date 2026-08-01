using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

public class AdminSubscriptionGrantT13Tests
{
    private static async Task<Guid> WalletAsync(PaymentTestDb t, OwnerType ownerType = OwnerType.User)
    {
        var id = Guid.NewGuid(); t.Db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OwnerType = ownerType, OwnerId = id, PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active, UpdatedAt = DateTime.UtcNow }); await t.Db.SaveChangesAsync(); return id;
    }
    [Fact]
    public async Task Grant_Now_SnapshotsAndResolves()
    {
        using var t = new PaymentTestDb(); var owner = await WalletAsync(t); var plan = await t.NewContext().Plans.SingleAsync(p => p.Code == "plus");
        var sub = await new SubscriptionService(t.NewContext()).GrantAsync(OwnerType.User, owner, plan.Id, 30, null, "grant-now");
        Assert.Equal(SubscriptionSource.AdminGrant, sub.Source); Assert.Null(sub.OrderId); Assert.NotEmpty(sub.EntitlementHash);
        Assert.Equal(sub.Id, (await new EntitlementResolver(t.NewContext()).ResolveAsync(OwnerType.User, owner)).SubscriptionId);
        Assert.Single(await t.NewContext().SubscriptionEvents.Where(e => e.SubscriptionId == sub.Id && e.EventType == "Activated").ToListAsync());
    }
    [Fact]
    public async Task Grant_Future_IsNotResolved_AndIdempotencyReturnsSameRow()
    {
        using var t = new PaymentTestDb(); var owner = await WalletAsync(t); var plan = await t.NewContext().Plans.SingleAsync(p => p.Code == "plus"); var at = DateTime.UtcNow.AddDays(2);
        var service = new SubscriptionService(t.NewContext()); var a = await service.GrantAsync(OwnerType.User, owner, plan.Id, 30, at, "future"); var b = await service.GrantAsync(OwnerType.User, owner, plan.Id, 30, at, "future");
        Assert.Equal(a.Id, b.Id); Assert.NotEqual(a.Id, (await new EntitlementResolver(t.NewContext()).ResolveAsync(OwnerType.User, owner)).SubscriptionId);
        Assert.Single(await t.NewContext().Subscriptions.Where(s => s.AdminGrantIdempotencyKey == "future").ToListAsync());
    }
    [Fact]
    public async Task Grant_RejectsAudienceWall()
    {
        using var t = new PaymentTestDb(); var owner = await WalletAsync(t); var b2b = await t.NewContext().Plans.FirstAsync(p => p.Audience == PlanAudience.B2B);
        await Assert.ThrowsAsync<ArgumentException>(() => new SubscriptionService(t.NewContext()).GrantAsync(OwnerType.User, owner, b2b.Id, 30, null, "bad"));
    }
}
