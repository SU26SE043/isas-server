using System.Reflection;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// DB4 — CreditReservationReconciler đối soát BẤT BIẾN:
//   credit_accounts.reserved_credits == count(credit_reservations status=Reserved) CÙNG owner.
// Quét từ phía credit_accounts (bắt cả ví reserved>0 mà count=0), sửa drift bằng ExecuteUpdate.
// SCOPE = core Payment-DB: reservation có sẵn owner_type/owner_id → KHÔNG gọi InterviewService.
public class CreditReservationReconcilerTests
{
    // Gọi ScanOnceAsync (private) 1 nhịp — cùng idiom SettlementReconcilerTests/StuckAnswerRepublisherTests.
    private static async Task ScanOnce(CreditReservationReconciler r)
    {
        var mi = typeof(CreditReservationReconciler)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    // ServiceProvider thật để CreateScope() trả DbContext dùng CHUNG connection với harness (như sweeper test).
    private static (CreditReservationReconciler r, ServiceProvider provider) Build(
        PaymentTestDb tdb, bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o
            .UseSqlite(tdb.Connection)
            .UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var r = new CreditReservationReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReconcileSettings { Enabled = enabled, ScanIntervalSeconds = 120 }),
            NullLogger<CreditReservationReconciler>.Instance);
        return (r, provider);
    }

    private static CreditAccount SeedAccount(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int reservedCredits, int remaining = 10)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = reservedCredits,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(acc);
        return acc;
    }

    private static void SeedReservation(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, ReservationStatus status)
    {
        tdb.Db.CreditReservations.Add(new CreditReservation
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            SessionId = Guid.NewGuid(),
            Status = status,
            CreatedAt = DateTime.UtcNow
        });
    }

    // Đối soát mọi kiểu drift về đúng count(Reserved) + không ví nào âm.
    [Fact]
    public async Task Reconcile_SuaMoiKieuDrift_VeCountReservedThat()
    {
        using var tdb = new PaymentTestDb();

        // (a) reserved CAO hơn count thật: reserved=5, chỉ 2 reservation Reserved (Consumed/Released không tính)
        var ownerA = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, ownerA, reservedCredits: 5);
        SeedReservation(tdb, OwnerType.User, ownerA, ReservationStatus.Reserved);
        SeedReservation(tdb, OwnerType.User, ownerA, ReservationStatus.Reserved);
        SeedReservation(tdb, OwnerType.User, ownerA, ReservationStatus.Consumed);
        SeedReservation(tdb, OwnerType.User, ownerA, ReservationStatus.Released);

        // (b) reserved THẤP hơn count thật: reserved=0, có 3 reservation Reserved
        var ownerB = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.Org, ownerB, reservedCredits: 0);
        SeedReservation(tdb, OwnerType.Org, ownerB, ReservationStatus.Reserved);
        SeedReservation(tdb, OwnerType.Org, ownerB, ReservationStatus.Reserved);
        SeedReservation(tdb, OwnerType.Org, ownerB, ReservationStatus.Reserved);

        // (c) reserved>0 nhưng 0 reservation Reserved (count=0) → phải về 0
        var ownerC = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, ownerC, reservedCredits: 4);
        SeedReservation(tdb, OwnerType.User, ownerC, ReservationStatus.Consumed);

        // (d) ví ĐÚNG sẵn: reserved=1, 1 reservation Reserved → KHÔNG đổi
        var ownerD = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.Org, ownerD, reservedCredits: 1);
        SeedReservation(tdb, OwnerType.Org, ownerD, ReservationStatus.Reserved);

        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb);
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();

        // Bất biến giữ trên MỌI ví: reserved_credits == count(Reserved) và không âm.
        foreach (var acc in await read.CreditAccounts.ToListAsync())
        {
            var count = await read.CreditReservations.CountAsync(x =>
                x.OwnerType == acc.OwnerType && x.OwnerId == acc.OwnerId &&
                x.Status == ReservationStatus.Reserved);
            Assert.Equal(count, acc.ReservedCredits);
            Assert.True(acc.ReservedCredits >= 0);
        }

        // Giá trị cụ thể (chống-âm + owner-scoping: ownerA/ownerC cùng User nhưng độc lập).
        Assert.Equal(2, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerA)).ReservedCredits);
        Assert.Equal(3, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerB)).ReservedCredits);
        Assert.Equal(0, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerC)).ReservedCredits);
        Assert.Equal(1, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerD)).ReservedCredits);
    }

    // Idempotent: sau khi sửa drift vòng 1, quét lần 2 KHÔNG đổi gì (đã khớp count).
    [Fact]
    public async Task Reconcile_Idempotent_LanHaiKhongDoi()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reservedCredits: 9);   // drift cố ý
        SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved);
        SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb);
        using (provider)
        {
            await ScanOnce(r);   // 9 → 2
            using (var mid = tdb.NewContext())
                Assert.Equal(2, (await mid.CreditAccounts.SingleAsync(a => a.OwnerId == owner)).ReservedCredits);

            var before = (await tdb.NewContext().CreditAccounts.SingleAsync(a => a.OwnerId == owner)).UpdatedAt;

            await ScanOnce(r);   // đã khớp → không đổi

            using var read = tdb.NewContext();
            var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
            Assert.Equal(2, acc.ReservedCredits);
            Assert.Equal(before, acc.UpdatedAt);   // không ghi lại (đã khớp → skip, không ExecuteUpdate)
        }
    }

    // Enabled=false → reconciler no-op, drift GIỮ nguyên (safe-disable).
    [Fact]
    public async Task Reconcile_Disabled_KhongSuaDrift()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reservedCredits: 7);   // drift, KHÔNG có reservation nào
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, enabled: false);
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(7, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner)).ReservedCredits);
    }

    // 0 ví → không nổ (guard rỗng).
    [Fact]
    public async Task Reconcile_KhongCoVi_KhongNo()
    {
        using var tdb = new PaymentTestDb();

        var (r, provider) = Build(tdb);
        using (provider)
            await ScanOnce(r);   // không ném

        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditAccounts.CountAsync());
    }
}
