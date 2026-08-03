using Isas.PaymentService.Services;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

public class EntitlementResolverTests
{
    [Fact]
    public async Task UserWithoutSubscription_GetsFreeDefault()
    {
        using var db = new PaymentTestDb();
        var result = await new EntitlementResolver(db.Db).ResolveAsync(OwnerType.User, Guid.NewGuid());
        Assert.Equal("free", result.TierCode);
        Assert.Equal("free-default", result.Source);
    }

    [Fact]
    public async Task OrgWithoutSubscription_GetsStarterDefault()
    {
        using var db = new PaymentTestDb();
        var result = await new EntitlementResolver(db.Db).ResolveAsync(OwnerType.Org, Guid.NewGuid());
        Assert.Equal("starter", result.TierCode);
    }

    [Fact]
    public async Task MissingDefaultCatalogRow_FallsBackToCompiledFreeEntitlement()
    {
        using var db = new PaymentTestDb();
        db.Db.Plans.RemoveRange(db.Db.Plans);
        await db.Db.SaveChangesAsync();

        var result = await new EntitlementResolver(db.NewContext()).ResolveAsync(OwnerType.User, Guid.NewGuid());

        Assert.Equal("free", result.TierCode);
        Assert.Equal(InterviewFunding.Credit, result.InterviewFunding);
        Assert.Equal("free-default", result.Source);
    }
}
