using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Tests;

// DB14 — audit updated_at trên practice_sessions (đóng dấu tự động khi session bị SỬA). Kiểm 2 đường ghi:
//   • tracked SaveChanges → override InterviewDbContext stamp updated_at.
//   • set-based ExecuteUpdate (SessionAbandonSweeper flip status) → .SetProperty(UpdatedAt) đẩy updated_at.
public class SessionUpdatedAtTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddMinutes(-30);

    // (1) TRACKED SaveChanges: sửa session (Status) qua change-tracker → override stamp updated_at mới.
    //     Insert (Added) KHÔNG stamp → giữ Old.
    [Fact]
    public async Task TrackedUpdate_StampsSessionUpdatedAt()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        session.UpdatedAt = Old;
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();   // Added → override KHÔNG stamp → giữ Old

        using (var ctx = t.NewContext())
        {
            var loaded = await ctx.PracticeSessions.SingleAsync(s => s.Id == session.Id);
            Assert.True(loaded.UpdatedAt < Old.AddMinutes(1),
                "insert (Added) KHÔNG được stamp updated_at về now");
            loaded.Status = SessionStatus.Scored;   // Modified
            await ctx.SaveChangesAsync();           // override stamp updated_at = now
        }

        using var read = t.NewContext();
        var after = await read.PracticeSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.True(after.UpdatedAt > Old.AddMinutes(1),
            $"updated_at phải tiến lên sau tracked update (Old={Old:o}, now={after.UpdatedAt:o})");
    }

    // (2) EXECUTE-UPDATE: SessionAbandonSweeper flip practice_sessions.status Ready/InProgress→SessionAbandoned
    //     bằng ExecuteUpdate (bỏ qua SaveChanges override) → .SetProperty(UpdatedAt) phải đẩy updated_at.
    [Fact]
    public async Task ExecuteUpdate_SweeperAbandon_StampsSessionUpdatedAt()
    {
        using var t = new TestDb();
        // B2B InProgress quá hạn nhận bài, 0 answer → sweeper đóng SessionAbandoned qua ExecuteUpdate.
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress,
            campaignId: Guid.NewGuid(), deadline: DateTime.UtcNow.AddMinutes(-5));
        session.UpdatedAt = Old;
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        await ScanOnce(Build(t));

        using var read = t.NewContext();
        var after = await read.PracticeSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.Equal(SessionStatus.SessionAbandoned, after.Status);
        Assert.True(after.UpdatedAt > Old.AddMinutes(1),
            $"updated_at phải tiến lên sau ExecuteUpdate sweeper abandon (Old={Old:o}, now={after.UpdatedAt:o})");
    }

    // Build SessionAbandonSweeper trên DbContext dùng CHUNG connection (mirror SessionAbandonSweeperTests).
    private static SessionAbandonSweeper Build(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();
        return new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ScoringOptions { B2CInactivityMinutes = 120 }),
            NullLogger<SessionAbandonSweeper>.Instance);
    }

    private static async Task ScanOnce(SessionAbandonSweeper s)
    {
        var mi = typeof(SessionAbandonSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(s, new object[] { CancellationToken.None })!;
    }
}
