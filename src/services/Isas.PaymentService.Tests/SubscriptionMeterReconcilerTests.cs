using System.Data.Common;
using System.Reflection;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// M1 — SubscriptionMeterReconciler đối soát BẤT BIẾN của quota thuê bao:
//   subscription_meters.reserved_count == count(reservations Reserved  cùng (sub, kỳ), funded_by=SubscriptionMetered)
//   subscription_meters.used_count     == count(reservations Consumed  cùng (sub, kỳ), funded_by=SubscriptionMetered)
// Job này GHI ĐÈ cột tiền nên phải khoá bằng test: tiền lệ DB21 cho thấy chính "job sửa drift"
// mới là thứ tạo ra drift khi thiếu CAS. Trước vòng này job có 0 test.
/// <summary>Harness dùng chung (TierContractGuardTests cũng cần chạy 1 nhịp reconciler).</summary>
internal static class SubscriptionMeterReconcilerHarness
{
    public static async Task ScanOnce(SubscriptionMeterReconciler r)
    {
        var mi = typeof(SubscriptionMeterReconciler)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    public static (SubscriptionMeterReconciler r, ServiceProvider provider) Build(
        PaymentTestDb tdb, bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o
            .UseSqlite(tdb.Connection)
            .UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();
        var r = new SubscriptionMeterReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReconcileSettings { Enabled = enabled, ScanIntervalSeconds = 120 }),
            NullLogger<SubscriptionMeterReconciler>.Instance);
        return (r, provider);
    }
}

public class SubscriptionMeterReconcilerTests
{
    private static Task ScanOnce(SubscriptionMeterReconciler r) => SubscriptionMeterReconcilerHarness.ScanOnce(r);

    private static (SubscriptionMeterReconciler r, ServiceProvider provider) Build(
        PaymentTestDb tdb, bool enabled = true) => SubscriptionMeterReconcilerHarness.Build(tdb, enabled);

    private static readonly DateTime Period = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    // Seed 1 subscription Metered còn hạn + 1 dòng meter với counter CỐ Ý lệch so với reservation thật.
    private static async Task<Guid> SeedAsync(PaymentTestDb t, int meterReserved, int meterUsed,
        int realReserved, int realConsumed)
    {
        var owner = Guid.NewGuid(); var subId = Guid.NewGuid(); var now = DateTime.UtcNow;
        t.Db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active, RemainingCredits = 0, UpdatedAt = now });
        t.Db.Subscriptions.Add(new Subscription { Id = subId, OwnerType = OwnerType.User, OwnerId = owner,
            Audience = PlanAudience.B2C, TierCode = "plus", TierRank = 1, InterviewFunding = InterviewFunding.Metered,
            MonthlyQuota = 30, EntitlementSnapshot = "{}", EntitlementHash = "x", ActivatedAt = now.AddMinutes(-1),
            StartedAt = now.AddMinutes(-1), ExpiresAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now });
        t.Db.SubscriptionMeters.Add(new SubscriptionMeter { SubscriptionId = subId, PeriodStart = Period,
            ReservedCount = meterReserved, UsedCount = meterUsed, UpdatedAt = now });

        for (var i = 0; i < realReserved + realConsumed; i++)
            t.Db.CreditReservations.Add(new CreditReservation
            {
                Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner, SessionId = Guid.NewGuid(),
                Status = i < realReserved ? ReservationStatus.Reserved : ReservationStatus.Consumed,
                FundedBy = ReservationFunding.SubscriptionMetered,
                MeteredSubscriptionId = subId, MeteredPeriodStart = Period,
                PaymentMode = PaymentMode.Prepaid, CreatedAt = now
            });
        await t.Db.SaveChangesAsync();
        return subId;
    }

    [Fact]
    public async Task ReservedDrift_DuocSuaVeDungSoReservationThat()
    {
        using var t = new PaymentTestDb();
        var subId = await SeedAsync(t, meterReserved: 5, meterUsed: 0, realReserved: 2, realConsumed: 0);
        var (r, provider) = Build(t); using (provider)
        {
            await ScanOnce(r);
        }
        using var read = t.NewContext();
        var meter = await read.SubscriptionMeters.SingleAsync(m => m.SubscriptionId == subId);
        Assert.Equal(2, meter.ReservedCount);
    }

    // Đây là vế MỚI thêm ở vòng vá. Thiếu nó, một consume bị rớt (meter update khớp 0 row, code chỉ
    // log rồi vẫn commit) sẽ KHÔNG BAO GIỜ được sửa ⇒ khách được lượt miễn phí vĩnh viễn.
    [Fact]
    public async Task UsedDrift_DuocSuaVeDungSoConsumeThat()
    {
        using var t = new PaymentTestDb();
        var subId = await SeedAsync(t, meterReserved: 0, meterUsed: 0, realReserved: 0, realConsumed: 3);
        var (r, provider) = Build(t); using (provider)
        {
            await ScanOnce(r);
        }
        using var read = t.NewContext();
        var meter = await read.SubscriptionMeters.SingleAsync(m => m.SubscriptionId == subId);
        Assert.Equal(3, meter.UsedCount);
        Assert.Equal(0, meter.ReservedCount);
    }

    // ĐUA THẬT (mẫu DB21): chen một "reserve vừa commit" vào ĐÚNG khe giữa câu COUNT và ExecuteUpdate.
    // Reconciler khi đó cầm snapshot ReservedCount ĐÃ CŨ:
    //   - KHÔNG guard CAS → ghi đè bằng count cũ (0) ⇒ xoá mất chỗ giữ vừa tạo ⇒ quota bốc hơi.
    //   - CÓ guard CAS  → WHERE reserved_count = <snapshot cũ> khớp 0 row ⇒ bỏ qua, chỗ giữ còn nguyên.
    // Chen TRƯỚC câu COUNT thì vô nghĩa: count sẽ thấy luôn reservation mới nên không phân biệt được.
    [Fact]
    public async Task CAS_KhongGhiDeChoGiuVuaReserve_KhiDuaGiuaCountVaUpdate()
    {
        using var t = new PaymentTestDb();
        var subId = await SeedAsync(t, meterReserved: 1, meterUsed: 0, realReserved: 0, realConsumed: 0);

        // Ghi bằng SQL thô trên CHÍNH connection đang mở (SQLite không cho tạo DbContext khi reader còn
        // sống); copy owner/kỳ TỪ BẢNG để khỏi đoán cách EF serialize Guid/DateTime — đoán sai là vỡ
        // FK composite (owner_type, owner_id) hoặc CHECK ck_reservation_metered_consistency.
        var interceptor = new ReserveRacesAfterCountInterceptor(async cmd =>
        {
            await using var bump = cmd.Connection!.CreateCommand();
            bump.CommandText = """
                UPDATE subscription_meters SET reserved_count = reserved_count + 1;
                INSERT INTO credit_reservations
                    (id, owner_type, owner_id, session_id, status, funded_by, payment_mode,
                     metered_subscription_id, metered_period_start, created_at, updated_at)
                SELECT $rid, a.owner_type, a.owner_id, $sid, 'Reserved', 'SubscriptionMetered', 'Prepaid',
                       m.subscription_id, m.period_start, $now, $now
                FROM credit_accounts a, subscription_meters m LIMIT 1;
                """;
            AddParam(bump, "$rid", Guid.NewGuid().ToString());
            AddParam(bump, "$sid", Guid.NewGuid().ToString());
            AddParam(bump, "$now", DateTime.UtcNow.ToString("o"));
            await bump.ExecuteNonQueryAsync();
        });

        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o
            .UseSqlite(t.Connection)
            .AddInterceptors(interceptor)
            .UseSnakeCaseNamingConvention());
        using var provider = services.BuildServiceProvider();
        var r = new SubscriptionMeterReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReconcileSettings { Enabled = true, ScanIntervalSeconds = 120 }),
            NullLogger<SubscriptionMeterReconciler>.Instance);

        await ScanOnce(r);

        Assert.True(interceptor.Fired, "Interceptor phải chen được vào giữa COUNT và UPDATE.");
        using var read = t.NewContext();
        var meter = await read.SubscriptionMeters.SingleAsync(m => m.SubscriptionId == subId);
        Assert.Equal(2, meter.ReservedCount); // chỗ giữ vừa tạo còn nguyên; không guard → bị kéo về 0
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p);
    }

    private sealed class ReserveRacesAfterCountInterceptor(Func<DbCommand, Task> race) : DbCommandInterceptor
    {
        private bool _done;
        public bool Fired => _done;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_done && command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase)
                       && command.CommandText.Contains("credit_reservations", StringComparison.OrdinalIgnoreCase))
            {
                _done = true;
                await race(command);
            }
            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Disabled_KhongDungToiMeter()
    {
        using var t = new PaymentTestDb();
        var subId = await SeedAsync(t, meterReserved: 9, meterUsed: 0, realReserved: 0, realConsumed: 0);
        var (r, provider) = Build(t, enabled: false); using (provider)
        {
            await ScanOnce(r);
        }
        using var read = t.NewContext();
        Assert.Equal(9, (await read.SubscriptionMeters.SingleAsync(m => m.SubscriptionId == subId)).ReservedCount);
    }
}
