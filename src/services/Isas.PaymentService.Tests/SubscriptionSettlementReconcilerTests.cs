using System.Reflection;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

public class SubscriptionSettlementReconcilerTests
{
    [Fact]
    public async Task PaidSubscriptionOrderWithoutEntitlement_IsLoggedForManualReconciliation()
    {
        using var tdb = new PaymentTestDb();
        tdb.Db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = Guid.NewGuid(),
            Kind = OrderKind.SubscriptionPurchase, Status = OrderStatus.Paid, AmountVnd = 99_000,
            PayosOrderCode = 991, ExpiredAt = DateTime.UtcNow, PaidAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o.UseSqlite(tdb.Connection).UseSnakeCaseNamingConvention());
        using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<SubscriptionSettlementReconciler>();
        var reconciler = new SubscriptionSettlementReconciler(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReconcileSettings { Enabled = true }), logger);

        var scan = typeof(SubscriptionSettlementReconciler).GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)scan.Invoke(reconciler, [CancellationToken.None])!;

        Assert.Contains(logger.Messages, message => message.Contains("has no subscription", StringComparison.Ordinal));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
