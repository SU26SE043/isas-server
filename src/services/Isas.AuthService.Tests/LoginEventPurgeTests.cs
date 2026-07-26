using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Tests;

public sealed class LoginEventPurgeTests
{
    [Fact]
    public async Task Purge_DeletesOnlyEventsOlderThanRetentionBoundary()
    {
        using var t = new AuthTestDb();
        var now = DateTime.UtcNow;
        t.Db.LoginEvents.AddRange(
            new LoginEvent { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Method = LoginMethod.Password, CreatedAt = now.AddDays(-366) },
            new LoginEvent { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Method = LoginMethod.Google, CreatedAt = now.AddDays(-365) },
            new LoginEvent { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Method = LoginMethod.MagicLink, CreatedAt = now.AddDays(-1) });
        await t.Db.SaveChangesAsync();

        var deleted = await LoginEventPurge.PurgeAsync(t.Db, now, new LoginEventRetentionSettings());

        Assert.Equal(1, deleted);
        Assert.Equal(2, await t.Db.LoginEvents.CountAsync());
    }
}
