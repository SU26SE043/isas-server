using System.Reflection;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// DB18 (DB4b) — OrphanReservationReconciler: release reservation Reserved mà session Interview KHÔNG BAO
// GIỜ được tạo (crash giữa reserve↔insert lúc Start). Xác minh DƯƠNG qua IInterviewSessionClient trước khi
// release; Interview down → skip vòng, KHÔNG release ai (an toàn tối thượng). Idempotent/absorbing PAY-11.
public class OrphanReservationReconcilerTests
{
    // Gọi ScanOnceAsync (private) 1 nhịp — cùng idiom CreditReservationReconcilerTests.
    private static async Task ScanOnce(OrphanReservationReconciler r)
    {
        var mi = typeof(OrphanReservationReconciler)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    // Provider thật: DbContext (chung connection harness) + CreditAccountService scoped + mock Interview client.
    private static (OrphanReservationReconciler r, ServiceProvider provider) Build(
        PaymentTestDb tdb, IInterviewSessionClient client, bool enabled = true, int thresholdMinutes = 10)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o
            .UseSqlite(tdb.Connection)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ICreditAccountService, CreditAccountService>();
        services.AddSingleton(client);
        var provider = services.BuildServiceProvider();

        var r = new OrphanReservationReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OrphanReconcileSettings
            {
                Enabled = enabled,
                ScanIntervalSeconds = 120,
                OrphanThresholdMinutes = thresholdMinutes,
                BatchSize = 200
            }),
            NullLogger<OrphanReservationReconciler>.Instance);
        return (r, provider);
    }

    private static IInterviewSessionClient Client(params Guid[] existing)
    {
        var m = new Mock<IInterviewSessionClient>();
        m.Setup(c => c.GetExistingSessionsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing.ToHashSet());
        return m.Object;
    }

    private static void SeedAccount(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int reserved, int remaining = 10,
        PaymentMode mode = PaymentMode.Prepaid)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = mode,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = reserved,
            UpdatedAt = DateTime.UtcNow
        });
    }

    // Trả sessionId để test tham chiếu. createdAt set thủ công để backdate (test threshold orphan).
    private static Guid SeedReservation(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, ReservationStatus status, DateTime createdAt)
    {
        var sessionId = Guid.NewGuid();
        tdb.Db.CreditReservations.Add(new CreditReservation
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            SessionId = sessionId,
            Status = status,
            CreatedAt = createdAt
        });
        return sessionId;
    }

    private static readonly DateTime Old = DateTime.UtcNow.AddMinutes(-30);   // quá ngưỡng 10' → orphan-cand
    private static readonly DateTime Fresh = DateTime.UtcNow.AddMinutes(-1);  // trong ngưỡng → chưa xét

    // Orphan: Reserved quá ngưỡng + Interview XÁC NHẬN không tồn tại → release + ví hoàn (prepaid).
    [Fact]
    public async Task Orphan_SessionKhongTonTai_Release_ViHoan()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var orphan = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        // Interview trả rỗng → orphan không nằm trong existing → release.
        var (r, provider) = Build(tdb, Client(/* existing = none */));
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        var resv = await read.CreditReservations.SingleAsync(x => x.SessionId == orphan);
        Assert.Equal(ReservationStatus.Released, resv.Status);

        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(0, acc.ReservedCredits);    // reserved−1
        Assert.Equal(10, acc.RemainingCredits);  // remaining+1 (prepaid hoàn chỗ giữ)
    }

    // Bao cả B2B (owner=Org): reconciler không phân biệt owner, chỉ theo session-existence.
    [Fact]
    public async Task Orphan_OwnerOrg_CungRelease()
    {
        using var tdb = new PaymentTestDb();
        var org = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.Org, org, reserved: 1, remaining: 5);
        var orphan = SeedReservation(tdb, OwnerType.Org, org, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client());
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Released,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == orphan)).Status);
        Assert.Equal(0, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == org)).ReservedCredits);
    }

    // Session TỒN TẠI (Interview xác nhận) → KHÔNG release, giữ Reserved + ví nguyên.
    [Fact]
    public async Task SessionTonTai_KhongRelease()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var live = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        // Interview trả existing CÓ chứa live → không phải orphan.
        var (r, provider) = Build(tdb, Client(live));
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == live)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(9, acc.RemainingCredits);
    }

    // Trong ngưỡng tuổi (insert có thể đang dở) → KHÔNG xét/không gọi Interview/không release.
    [Fact]
    public async Task TrongThreshold_KhongScan()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var fresh = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Fresh);
        await tdb.Db.SaveChangesAsync();

        var mock = new Mock<IInterviewSessionClient>();
        mock.Setup(c => c.GetExistingSessionsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid>());

        var (r, provider) = Build(tdb, mock.Object);
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == fresh)).Status);
        // Không có ứng viên (quá-ngưỡng) → KHÔNG gọi Interview.
        mock.Verify(c => c.GetExistingSessionsAsync(
            It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // AN TOÀN QUAN TRỌNG NHẤT: Interview DOWN (client ném) → ScanOnce ném/skip → KHÔNG release AI CẢ.
    [Fact]
    public async Task InterviewDown_KhongReleaseAiCa()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 2, remaining: 8);
        var a = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        var b = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var down = new Mock<IInterviewSessionClient>();
        down.Setup(c => c.GetExistingSessionsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InterviewServiceException("Interview down"));

        var (r, provider) = Build(tdb, down.Object);
        using (provider)
            await Assert.ThrowsAsync<InterviewServiceException>(() => ScanOnce(r));

        // KHÔNG xác minh được → KHÔNG release ai: cả 2 vẫn Reserved, ví nguyên.
        using var read = tdb.NewContext();
        Assert.All(await read.CreditReservations.Where(x => x.SessionId == a || x.SessionId == b).ToListAsync(),
            x => Assert.Equal(ReservationStatus.Reserved, x.Status));
        var acc = await read.CreditAccounts.SingleAsync(acc => acc.OwnerId == owner);
        Assert.Equal(2, acc.ReservedCredits);
        Assert.Equal(8, acc.RemainingCredits);
    }

    // Idempotent: quét 2 lần → lần 2 no-op (reservation đã Released). Reservation Consumed KHÔNG bị release.
    [Fact]
    public async Task Idempotent_LanHaiNoOp_VaKhongDungConsumed()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var orphan = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        // Reservation đã Consumed (đã tiêu thật) — quá ngưỡng nhưng KHÔNG phải Reserved → không đụng.
        var consumed = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Consumed, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client(/* none exist */));
        using (provider)
        {
            await ScanOnce(r);   // release orphan
            using (var mid = tdb.NewContext())
            {
                Assert.Equal(ReservationStatus.Released,
                    (await mid.CreditReservations.SingleAsync(x => x.SessionId == orphan)).Status);
                Assert.Equal(10, (await mid.CreditAccounts.SingleAsync(a => a.OwnerId == owner)).RemainingCredits);
            }

            await ScanOnce(r);   // lần 2: orphan đã Released → không còn là ứng viên → no-op

            using var read = tdb.NewContext();
            var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
            Assert.Equal(10, acc.RemainingCredits);   // KHÔNG hoàn oan lần 2
            Assert.Equal(0, acc.ReservedCredits);
            // Consumed giữ nguyên (absorbing PAY-11, không bị release).
            Assert.Equal(ReservationStatus.Consumed,
                (await read.CreditReservations.SingleAsync(x => x.SessionId == consumed)).Status);
        }
    }

    // Enabled=false → no-op, orphan GIỮ nguyên (safe-disable).
    [Fact]
    public async Task Disabled_KhongRelease()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var orphan = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client(), enabled: false);
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == orphan)).Status);
    }
}
