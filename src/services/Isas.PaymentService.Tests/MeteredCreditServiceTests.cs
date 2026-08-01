using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

public class MeteredCreditServiceTests
{
    private static async Task<Guid> SeedMeteredAsync(PaymentTestDb t, int quota, int credits = 0)
    {
        var owner = Guid.NewGuid(); var now = DateTime.UtcNow;
        t.Db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active, RemainingCredits = credits, UpdatedAt = now });
        t.Db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            Audience = PlanAudience.B2C, TierCode = "plus", TierRank = 1, InterviewFunding = InterviewFunding.Metered,
            MonthlyQuota = quota, EntitlementSnapshot = "{}", EntitlementHash = "x", ActivatedAt = now.AddMinutes(-1),
            StartedAt = now.AddMinutes(-1), ExpiresAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now });
        await t.Db.SaveChangesAsync(); return owner;
    }
    private static CreditAccountService Service(PaymentDbContext db) => new(
        db,
        entitlements: new EntitlementResolver(db),
        tiering: Options.Create(new TieringSettings { Enabled = true }));

    [Fact]
    public async Task Metered_QuotaExhausted_FallsBackToCredit()
    {
        using var t = new PaymentTestDb(); var owner = await SeedMeteredAsync(t, 1, 1);
        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());
        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());
        using var read = t.NewContext();
        Assert.Equal(1, await read.CreditReservations.CountAsync(r => r.FundedBy == ReservationFunding.SubscriptionMetered));
        Assert.Equal(1, await read.CreditReservations.CountAsync(r => r.FundedBy == ReservationFunding.Credit));
        var meter = await read.SubscriptionMeters.SingleAsync(); Assert.Equal(1, meter.ReservedCount);
        Assert.Equal(0, (await read.CreditAccounts.SingleAsync()).RemainingCredits);
    }

    [Fact]
    public async Task Metered_ReleaseAndConsume_UseReservationSnapshot()
    {
        using var t = new PaymentTestDb(); var owner = await SeedMeteredAsync(t, 2); var released = Guid.NewGuid(); var consumed = Guid.NewGuid();
        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, released);
        await Service(t.NewContext()).ReleaseAsync(released);
        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, consumed);
        // Simulate a later upgrade: settlement must still mutate the meter snapshot, not current entitlement.
        var sub = await t.Db.Subscriptions.SingleAsync(); sub.InterviewFunding = InterviewFunding.Credit; await t.Db.SaveChangesAsync();
        await Service(t.NewContext()).ConsumeAsync(consumed);
        using var read = t.NewContext(); var meter = await read.SubscriptionMeters.SingleAsync();
        Assert.Equal(1, meter.UsedCount); Assert.Equal(0, meter.ReservedCount);
        Assert.Empty(await read.CreditTransactions.ToListAsync());
    }

    [Fact]
    public async Task Metered_CancelAfterReserve_StillConsumesAndReleasesOriginalMeterSnapshot()
    {
        using var t = new PaymentTestDb(); var owner = await SeedMeteredAsync(t, 2);
        var consumed = Guid.NewGuid(); var released = Guid.NewGuid();
        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, consumed);
        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, released);

        Assert.True((await new SubscriptionService(t.NewContext()).CancelEffectiveAsync(OwnerType.User, owner)).Cancelled);
        await Service(t.NewContext()).ConsumeAsync(consumed);
        await Service(t.NewContext()).ReleaseAsync(released);

        using var read = t.NewContext(); var meter = await read.SubscriptionMeters.SingleAsync();
        Assert.Equal(1, meter.UsedCount); Assert.Equal(0, meter.ReservedCount);
        Assert.Empty(await read.CreditTransactions.ToListAsync());
    }

    [Fact]
    public async Task Metered_ConsumeAfterMonthBoundary_UsesOriginalPeriod()
    {
        using var t = new PaymentTestDb(); var owner = await SeedMeteredAsync(t, 2); var session = Guid.NewGuid();
        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, session);
        var original = new DateTime(DateTime.UtcNow.AddMonths(-1).Year, DateTime.UtcNow.AddMonths(-1).Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var reservation = await t.Db.CreditReservations.SingleAsync(); var currentMeter = await t.Db.SubscriptionMeters.SingleAsync();
        t.Db.SubscriptionMeters.Remove(currentMeter);
        t.Db.SubscriptionMeters.Add(new SubscriptionMeter { SubscriptionId = reservation.MeteredSubscriptionId!.Value,
            PeriodStart = original, ReservedCount = 1, UsedCount = 0 });
        reservation.MeteredPeriodStart = original; await t.Db.SaveChangesAsync();
        await Service(t.NewContext()).ConsumeAsync(session);
        using var read = t.NewContext(); var meter = await read.SubscriptionMeters.SingleAsync();
        Assert.Equal(original, meter.PeriodStart); Assert.Equal(1, meter.UsedCount); Assert.Equal(0, meter.ReservedCount);
    }

    [Fact]
    public async Task Metered_FiftyReserves_QuotaThirty_OnlyThirtyUseMeter()
    {
        using var t = new PaymentTestDb(); var owner = await SeedMeteredAsync(t, 30, 20);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            await using var db = t.NewContext();
            await Service(db).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());
        }));
        using var read = t.NewContext();
        Assert.Equal(30, await read.CreditReservations.CountAsync(r => r.FundedBy == ReservationFunding.SubscriptionMetered));
        Assert.Equal(20, await read.CreditReservations.CountAsync(r => r.FundedBy == ReservationFunding.Credit));
        var meter = await read.SubscriptionMeters.SingleAsync(); Assert.Equal(30, meter.ReservedCount); Assert.Equal(0, meter.UsedCount);
    }

    [Fact]
    public async Task Metered_SuspendedWallet_CannotReserveQuota()
    {
        using var t = new PaymentTestDb(); var owner = await SeedMeteredAsync(t, 2);
        var account = await t.Db.CreditAccounts.SingleAsync();
        account.Status = CreditAccountStatus.Suspended;
        await t.Db.SaveChangesAsync();

        var result = await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Insufficient, result.Outcome);
        using var read = t.NewContext();
        Assert.Empty(await read.CreditReservations.ToListAsync());
        Assert.Empty(await read.SubscriptionMeters.ToListAsync());
    }

    [Fact]
    public async Task TieringEnabled_B2BCreditSubscription_ChargesWallet()
    {
        using var t = new PaymentTestDb();
        var owner = Guid.NewGuid(); var now = DateTime.UtcNow;
        t.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.Org, OwnerId = owner,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active,
            RemainingCredits = 1, UpdatedAt = now
        });
        t.Db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.Org, OwnerId = owner,
            Audience = PlanAudience.B2B, TierCode = "business", TierRank = 1,
            InterviewFunding = InterviewFunding.Credit, EntitlementSnapshot = "{}", EntitlementHash = "x",
            ActivatedAt = now, StartedAt = now, ExpiresAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now
        });
        await t.Db.SaveChangesAsync();

        var result = await Service(t.NewContext()).ReserveAsync(OwnerType.Org, owner, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, result.Outcome);
        using var read = t.NewContext();
        Assert.Equal(ReservationFunding.Credit, (await read.CreditReservations.SingleAsync()).FundedBy);
        Assert.Equal(0, (await read.CreditAccounts.SingleAsync()).RemainingCredits);
    }
}
