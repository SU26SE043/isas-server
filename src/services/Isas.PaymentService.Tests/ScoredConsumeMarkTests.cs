using System.Reflection;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// R1 — mốc consume chỗ giữ của session Scored TÁCH khỏi cutover PONR1 (`ScoredConsumeFromUtc`).
/// Vì sao: mốc mặc định = giờ khởi động ⇒ TRÔI theo mỗi lần CI deploy (prod 2026-09-15 mốc = đúng lúc
/// container lên); còn dùng lại `ConsumeFromUtc` thì vô tình bật ngữ nghĩa PONR1 phía Payment (bỏ ngang
/// bị CONSUME) trong khi Interview vẫn thu tại Scored. Test khoá: (a) mốc riêng có tác dụng ở nhánh Scored,
/// (b) thứ tự ưu tiên Scored → ConsumeFromUtc → khởi động, (c) mốc riêng KHÔNG đụng gate PONR1 ở cả
/// CreditEventHandler lẫn nhánh SessionAbandoned của reconciler.
/// </summary>
public class ScoredConsumeMarkTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddMinutes(-30);

    private static async Task ScanOnce(OrphanReservationReconciler r)
    {
        var mi = typeof(OrphanReservationReconciler)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    private static DateTime ConsumeMark(OrphanReservationReconciler r) =>
        (DateTime)typeof(OrphanReservationReconciler)
            .GetField("_consumeFromUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(r)!;

    private static (OrphanReservationReconciler r, ServiceProvider provider) Build(
        PaymentTestDb tdb, IInterviewSessionClient client, OrphanReconcileSettings settings)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o.UseSqlite(tdb.Connection).UseSnakeCaseNamingConvention());
        services.AddScoped<ICreditAccountService, CreditAccountService>();
        services.AddSingleton(client);
        var provider = services.BuildServiceProvider();
        var r = new OrphanReservationReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(settings),
            NullLogger<OrphanReservationReconciler>.Instance);
        return (r, provider);
    }

    private static IInterviewSessionClient Client(Guid id, string status)
    {
        var m = new Mock<IInterviewSessionClient>();
        m.Setup(c => c.GetExistingSessionsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InterviewSessionsSnapshot(new HashSet<Guid> { id }, new Dictionary<Guid, string> { [id] = status }));
        return m.Object;
    }

    private static Guid Seed(PaymentTestDb tdb, Guid owner, DateTime createdAt)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner, PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active, RemainingCredits = 9, ReservedCredits = 1, UpdatedAt = DateTime.UtcNow
        });
        var sessionId = Guid.NewGuid();
        tdb.Db.CreditReservations.Add(new CreditReservation
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner, SessionId = sessionId,
            Status = ReservationStatus.Reserved, CreatedAt = createdAt
        });
        tdb.Db.SaveChanges();
        return sessionId;
    }

    // (a) Bản cũ: ConsumeFromUtc null ⇒ mốc = lúc khởi động ⇒ chỗ giữ 30' tuổi bị SKIP. Nay đặt
    // ScoredConsumeFromUtc lùi 1 ngày ⇒ Scored được CONSUME dù ConsumeFromUtc vẫn null.
    [Fact]
    public async Task ScoredConsumeFromUtc_TuongMinh_ConsumeScoredDuConsumeFromUtcNull()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var sessionId = Seed(tdb, owner, Old);

        var (r, provider) = Build(tdb, Client(sessionId, "Scored"), new OrphanReconcileSettings
        {
            ConsumeTerminalScored = true,
            ScoredConsumeFromUtc = DateTime.UtcNow.AddDays(-1)
            // ConsumeFromUtc = null (cấu hình production)
        });
        using (provider) await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Consumed,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == sessionId)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(9, acc.RemainingCredits);
        Assert.Single(await read.CreditTransactions.Where(t => t.SessionId == sessionId).ToListAsync());
    }

    // (a') Vẫn KHÔNG trừ hồi tố: chỗ giữ cũ hơn ScoredConsumeFromUtc → SKIP (OPS2).
    [Fact]
    public async Task ScoredConsumeFromUtc_ChoGiuCuHonMoc_VanSkip()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var sessionId = Seed(tdb, owner, Old);

        var (r, provider) = Build(tdb, Client(sessionId, "Scored"), new OrphanReconcileSettings
        {
            ConsumeTerminalScored = true,
            ScoredConsumeFromUtc = DateTime.UtcNow.AddMinutes(-5)   // sau lúc tạo chỗ giữ (−30')
        });
        using (provider) await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == sessionId)).Status);
    }

    // (b) Thứ tự ưu tiên: ScoredConsumeFromUtc → ConsumeFromUtc → khởi động.
    [Fact]
    public void ThuTuUuTien_Scored_RoiConsumeFromUtc_RoiKhoiDong()
    {
        using var tdb = new PaymentTestDb();
        var client = Mock.Of<IInterviewSessionClient>();
        var scored = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        var cutover = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var (both, p1) = Build(tdb, client, new OrphanReconcileSettings { ScoredConsumeFromUtc = scored, ConsumeFromUtc = cutover });
        using (p1) Assert.Equal(scored, ConsumeMark(both));

        var (onlyCutover, p2) = Build(tdb, client, new OrphanReconcileSettings { ConsumeFromUtc = cutover });
        using (p2) Assert.Equal(cutover, ConsumeMark(onlyCutover));

        var truoc = DateTime.UtcNow;
        var (none, p3) = Build(tdb, client, new OrphanReconcileSettings());
        using (p3) Assert.InRange(ConsumeMark(none), truoc, DateTime.UtcNow);
    }

    // (c1) Mốc riêng KHÔNG đụng gate PONR1 ở CreditEventHandler: ConsumeFromUtc null ⇒ bỏ ngang vẫn RELEASE
    // dù ScoredConsumeFromUtc đã đặt. Gộp hai mốc làm một là người bỏ ngang bị trừ tiền trong khi Interview
    // vẫn thu tại Scored.
    [Fact]
    public async Task Handler_ScoredMocTuongMinh_KhongBatPONR1_BoNgangVanRelease()
    {
        var sessionId = Guid.NewGuid();
        var credits = new Mock<ICreditAccountService>(MockBehavior.Strict);
        credits.Setup(c => c.ReleaseAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReleaseResult.Released(Guid.NewGuid()));

        var handler = new CreditEventHandler(credits.Object, Mock.Of<ILogger<CreditEventHandler>>(),
            Options.Create(new OrphanReconcileSettings { ScoredConsumeFromUtc = DateTime.UtcNow.AddDays(-1) }));

        var json = System.Text.Json.JsonSerializer.Serialize(new Isas.PaymentService.DTOs.SessionAbandonedMessage
        {
            SessionId = sessionId, CandidateId = Guid.NewGuid(), Reason = "inactivity_timeout", AbandonedAt = DateTime.UtcNow
        });
        await handler.HandleAsync(CreditEventHandler.SessionAbandonedRoutingKey, json);

        credits.Verify(c => c.ReleaseAsync(sessionId, It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(c => c.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (c2) Nhánh SessionAbandoned của reconciler cũng chỉ CONSUME khi ConsumeFromUtc (cutover) đặt —
    // ScoredConsumeFromUtc một mình ⇒ vẫn RELEASE.
    [Fact]
    public async Task Reconciler_ScoredMocTuongMinh_SessionAbandoned_VanRelease()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var sessionId = Seed(tdb, owner, Old);

        var (r, provider) = Build(tdb, Client(sessionId, "SessionAbandoned"), new OrphanReconcileSettings
        {
            ConsumeTerminalScored = true,
            ScoredConsumeFromUtc = DateTime.UtcNow.AddDays(-1)
        });
        using (provider) await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Released,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == sessionId)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(10, acc.RemainingCredits);   // hoàn 9+1
        Assert.Equal(0, acc.ReservedCredits);
    }
}
