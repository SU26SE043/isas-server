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

    // Dựng reconciler với settings TRUYỀN THẲNG — dùng cho ca cần `ConsumeFromUtc` thật sự NULL
    // (đúng cấu hình production). Build() bên dưới là lớp tiện dụng bọc quanh nó.
    private static (OrphanReservationReconciler r, ServiceProvider provider) BuildWith(
        PaymentTestDb tdb, IInterviewSessionClient client, OrphanReconcileSettings settings)
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
            Options.Create(settings),
            NullLogger<OrphanReservationReconciler>.Instance);
        return (r, provider);
    }

    // Provider thật: DbContext (chung connection harness) + CreditAccountService scoped + mock Interview client.
    // R1 — consumeFromUtc mặc định lùi 1 GIỜ để mọi test dùng mốc Old(-30') vẫn nằm SAU mốc (ca "chỗ giữ
    // mới, được phép consume"). Test nào cần ca "cũ hơn mốc" thì truyền mốc tường minh.
    // ⚠ Helper này LUÔN set ConsumeFromUtc ⇒ KHÔNG chạm nhánh mặc định `?? DateTime.UtcNow` của production.
    //   Nhánh đó được khoá riêng bởi 2 test `R1_MocMacDinh_*` (dùng BuildWith với ConsumeFromUtc=null).
    private static (OrphanReservationReconciler r, ServiceProvider provider) Build(
        PaymentTestDb tdb, IInterviewSessionClient client, bool enabled = true, int thresholdMinutes = 10,
        bool consumeTerminalScored = true, DateTime? consumeFromUtc = null)
        => BuildWith(tdb, client, new OrphanReconcileSettings
        {
            Enabled = enabled,
            ScanIntervalSeconds = 120,
            OrphanThresholdMinutes = thresholdMinutes,
            BatchSize = 200,
            ConsumeTerminalScored = consumeTerminalScored,
            ConsumeFromUtc = consumeFromUtc ?? DateTime.UtcNow.AddHours(-1)
        });

    // Đọc mốc đã chốt trong constructor (private readonly) — cùng idiom reflection với ScanOnce.
    private static DateTime ConsumeMark(OrphanReservationReconciler r) =>
        (DateTime)typeof(OrphanReservationReconciler)
            .GetField("_consumeFromUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(r)!;

    // Interview trả: các session TỒN TẠI + trạng thái từng cái (R1). Dùng cho ca có trạng thái.
    private static IInterviewSessionClient Client(params (Guid Id, string Status)[] sessions)
    {
        var snapshot = new InterviewSessionsSnapshot(
            sessions.Select(s => s.Id).ToHashSet(),
            sessions.ToDictionary(s => s.Id, s => s.Status, EqualityComparer<Guid>.Default));
        var m = new Mock<IInterviewSessionClient>();
        m.Setup(c => c.GetExistingSessionsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        return m.Object;
    }

    // Interview KHÔNG có session nào (mọi ứng viên = orphan DB18 cổ điển).
    private static IInterviewSessionClient EmptyClient() => Client();

    // R1 — mô phỏng Interview bản CŨ (trước R1): điền existingIds nhưng KHÔNG có states.
    private static IInterviewSessionClient LegacyClient(params Guid[] existing)
    {
        var snapshot = new InterviewSessionsSnapshot(
            existing.ToHashSet(), new Dictionary<Guid, string>());
        var m = new Mock<IInterviewSessionClient>();
        m.Setup(c => c.GetExistingSessionsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
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
        var (r, provider) = Build(tdb, EmptyClient());
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

        var (r, provider) = Build(tdb, EmptyClient());
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
        var (r, provider) = Build(tdb, Client((live, "InProgress")));
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
            .ReturnsAsync(new InterviewSessionsSnapshot(
                new HashSet<Guid>(), new Dictionary<Guid, string>()));

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

        var (r, provider) = Build(tdb, EmptyClient());
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

        var (r, provider) = Build(tdb, EmptyClient(), enabled: false);
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == orphan)).Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // R1 — session TỒN TẠI nhưng đã TERMINAL mà chỗ giữ vẫn Reserved (trước R1: KHÔNG AI DỌN → rò credit).
    // Đo được trên production 2026-07-20: 1 ca SessionAbandoned (user mất oan 1 credit) + 2 ca Scored
    // (org/user được buổi phỏng vấn miễn phí). Nguyên nhân: mất event settle; lỗ cần vá: thiếu lưới cuối.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    // Scored ⇒ ≥1 answer được AI chấm (AnswerService.cs:692 đẩy ca scoredCount==0 sang SessionAbandoned
    // TRƯỚC khi kịp set Scored) ⇒ đúng ngữ nghĩa PAY-1/PAY-13: phải THU tiền, không phải hoàn.
    [Fact]
    public async Task R1_Scored_ThiConsume_KhongHoanCredit()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var scored = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client((scored, "Scored")));
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Consumed,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == scored)).Status);

        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(0, acc.ReservedCredits);     // reserved−1
        Assert.Equal(9, acc.RemainingCredits);    // KHÔNG hoàn: consume ≠ release (đây là vế phân biệt M1)

        // Bút toán −1 phải có thật: thiếu nó thì bất biến remaining+reserved=Σdelta gãy.
        var ledger = await read.CreditTransactions.Where(t => t.OwnerId == owner).ToListAsync();
        Assert.Single(ledger);
        Assert.Equal(-1, ledger[0].Delta);
        Assert.Equal(CreditTransactionReason.Consume, ledger[0].Reason);
    }

    // B2B — ca production `ad20dae0` (org được buổi miễn phí). Owner không đổi cách xử lý.
    [Fact]
    public async Task R1_Scored_OwnerOrg_CungConsume()
    {
        using var tdb = new PaymentTestDb();
        var org = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.Org, org, reserved: 1, remaining: 5);
        var scored = SeedReservation(tdb, OwnerType.Org, org, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client((scored, "Scored")));
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Consumed,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == scored)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == org);
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(5, acc.RemainingCredits);
    }

    // SessionAbandoned — ca production `ee655e32` (user mất oan 1 credit) → hoàn chỗ giữ (E7).
    [Fact]
    public async Task R1_SessionAbandoned_ThiRelease()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var abandoned = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client((abandoned, "SessionAbandoned")));
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Released,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == abandoned)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(10, acc.RemainingCredits);   // hoàn chỗ giữ
    }

    // Failed = lỗi sinh câu hỏi → user không nhận được gì → release (BK12 vốn phát SessionAbandoned).
    [Fact]
    public async Task R1_Failed_ThiRelease()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var failed = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client((failed, "Failed")));
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Released,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == failed)).Status);
        Assert.Equal(10, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner)).RemainingCredits);
    }

    // Đang bay hợp lệ → TUYỆT ĐỐI không đụng. ⚠ "Completed" hiện là trạng thái CHẾT (không production site
    // nào GHI nó — chỉ AnswerService đọc để chặn upload); giữ ca test cho khớp enum + phòng thủ về sau.
    [Theory]
    [InlineData("GeneratingQuestions")]
    [InlineData("Ready")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    [InlineData("Scoring")]
    public async Task R1_DangBay_KhongDung(string status)
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var live = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client((live, status)));
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == live)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(9, acc.RemainingCredits);
        Assert.Empty(await read.CreditTransactions.Where(t => t.OwnerId == owner).ToListAsync());
    }

    // FAIL-SAFE: Interview thêm trạng thái mới mà Payment chưa biết → SKIP, KHÔNG đoán.
    // Đây là ca chặn nhánh `default → Consume` (M2).
    [Fact]
    public async Task R1_TrangThaiLa_Skip()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var weird = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client((weird, "SomeFutureStatus")));
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == weird)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(9, acc.RemainingCredits);
    }

    // Session TỒN TẠI nhưng KHÔNG có status trong states → "không biết" ⇒ SKIP (M6).
    [Fact]
    public async Task R1_ThieuStatus_Skip()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var live = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        // ExistingIds CÓ live, States KHÔNG có entry nào cho nó.
        var client = new Mock<IInterviewSessionClient>();
        client.Setup(c => c.GetExistingSessionsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InterviewSessionsSnapshot(
                new HashSet<Guid> { live }, new Dictionary<Guid, string>()));

        var (r, provider) = Build(tdb, client.Object);
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == live)).Status);
        Assert.Equal(9, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner)).RemainingCredits);
    }

    // 🔴 DEPLOY-SAFETY (M5) — Payment MỚI nói chuyện với Interview CŨ (chưa có R1 → không trả `states`).
    // Lệch phiên bản image LÀ CHUYỆN ĐÃ XẢY RA trên hệ này. Nếu tồn-tại bị suy từ `states` thay vì
    // `existingIds` thì MỌI session trông như không tồn tại → release cả session ĐANG THI.
    // Kỳ vọng: thoái lui ĐÚNG BẰNG hành vi trước R1 — orphan thật vẫn release, session tồn tại KHÔNG đụng.
    [Fact]
    public async Task R1_InterviewCu_KhongTraStates_ChiReleaseOrphan()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 2, remaining: 8);
        var live = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        var orphan = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, LegacyClient(live));   // existingIds=[live], states VẮNG
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        // Session đang thi: KHÔNG bị đụng (đây là vế sập nếu suy tồn-tại từ states).
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == live)).Status);
        // Orphan thật: vẫn được release như trước R1.
        Assert.Equal(ReservationStatus.Released,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == orphan)).Status);

        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(1, acc.ReservedCredits);     // chỉ orphan được nhả
        Assert.Equal(9, acc.RemainingCredits);
    }

    // Quyết định B — KHÔNG trừ tiền hồi tố: chỗ giữ cũ hơn mốc ConsumeFromUtc → SKIP (M7).
    // ⚠ Và KHÔNG release: release một buổi ĐÃ CHẤM = tặng buổi miễn phí, đúng bug R1 đang sửa.
    // Tồn đọng là hệ quả sự cố hạ tầng của chúng ta → để NGƯỜI đối soát tay (OPS2), không để máy tự quyết.
    [Fact]
    public async Task R1_ScoredCuHonMoc_Skip_KhongTruKhongRelease()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var oldScored = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        // Mốc = bây giờ ⇒ chỗ giữ Old(-30') nằm TRƯỚC mốc (mô phỏng mặc định "mốc khởi động dịch vụ").
        var (r, provider) = Build(tdb, Client((oldScored, "Scored")), consumeFromUtc: DateTime.UtcNow);
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == oldScored)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(9, acc.RemainingCredits);
        Assert.Empty(await read.CreditTransactions.Where(t => t.OwnerId == owner).ToListAsync());
    }

    // Quyết định A — công tắc riêng cho nhánh trừ tiền: tắt được ngay bằng env, khỏi rollback image.
    // Nhánh release vẫn phải chạy bình thường khi cờ tắt (nửa an toàn không bị tắt theo).
    [Fact]
    public async Task R1_ConsumeTat_ScoredSkip_NhungAbandonedVanRelease()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 2, remaining: 8);
        var scored = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        var abandoned = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(
            tdb, Client((scored, "Scored"), (abandoned, "SessionAbandoned")), consumeTerminalScored: false);
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,   // nhánh trừ tiền TẮT
            (await read.CreditReservations.SingleAsync(x => x.SessionId == scored)).Status);
        Assert.Equal(ReservationStatus.Released,   // nhánh hoàn tiền vẫn chạy
            (await read.CreditReservations.SingleAsync(x => x.SessionId == abandoned)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(9, acc.RemainingCredits);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // 🔴 NHÁNH MẶC ĐỊNH CỦA MỐC — `ConsumeFromUtc` KHÔNG cấu hình ⇒ lấy mốc KHỞI ĐỘNG reconciler.
    // Đây KHÔNG phải nhánh hiếm: quyết định B cố ý thiết kế để ops không phải cấu hình gì, nên trên
    // production `ConsumeFromUtc` sẽ LUÔN không được set ⇒ **mặc định chính là thứ duy nhất chặn trừ
    // tiền hồi tố**. Mọi test khác đi qua Build() vốn LUÔN set mốc tường minh ⇒ nhánh này từng KHÔNG
    // ĐƯỢC CHẠY LẦN NÀO. 2 test dưới ghim mốc mặc định từ CẢ HAI phía:
    //   · quá KHỨ (vd `?? DateTime.MinValue` — một default trông rất hợp lý: "chưa đặt = không giới
    //     hạn") ⇒ máy trừ credit hồi tố đúng những dòng tồn đọng phải để người đối soát tay;
    //   · quá TƯƠNG LAI (vd `?? DateTime.MaxValue`) ⇒ nhánh consume CHẾT IM LẶNG trên production —
    //     không lỗi, không log, chỗ giữ tiếp tục rò y như trước R1.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    // Phía QUÁ KHỨ (hành vi): không cấu hình mốc → chỗ giữ Scored có trước lúc khởi động phải SKIP,
    // KHÔNG trừ và KHÔNG release. Đây đúng ca 3 dòng tồn đọng đo được trên production 2026-07-20.
    [Fact]
    public async Task R1_MocMacDinh_ScoredTruocKhoiDong_Skip_KhongTruKhongRelease()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 1, remaining: 9);
        var tonDong = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        // ConsumeFromUtc KHÔNG set = ĐÚNG cấu hình production.
        var (r, provider) = BuildWith(tdb, Client((tonDong, "Scored")), new OrphanReconcileSettings
        {
            Enabled = true,
            ScanIntervalSeconds = 120,
            OrphanThresholdMinutes = 10,
            BatchSize = 200,
            ConsumeTerminalScored = true
            // ConsumeFromUtc = null  ← chính là nhánh cần khoá
        });
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        Assert.Equal(ReservationStatus.Reserved,
            (await read.CreditReservations.SingleAsync(x => x.SessionId == tonDong)).Status);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(9, acc.RemainingCredits);
        Assert.Empty(await read.CreditTransactions.Where(t => t.OwnerId == owner).ToListAsync());
    }

    // Phía TƯƠNG LAI (giá trị): mốc mặc định phải nằm ĐÚNG tại thời điểm dựng reconciler.
    // Không kiểm được bằng hành vi: chỗ giữ phải vừa cũ hơn ngưỡng orphan (10') vừa mới hơn mốc, mà mốc
    // mặc định = lúc dựng ⇒ không dựng được ca đó trong thời gian thực (`OrphanThresholdMinutes=0` cũng
    // không giúp: code coi `>0 ? … : 10`). Nên ghim thẳng GIÁ TRỊ, kẹp hai đầu.
    [Fact]
    public async Task R1_MocMacDinh_ChinhLaThoiDiemKhoiDong()
    {
        using var tdb = new PaymentTestDb();

        var truoc = DateTime.UtcNow;
        var (r, provider) = BuildWith(tdb, EmptyClient(), new OrphanReconcileSettings
        {
            Enabled = true, ScanIntervalSeconds = 120, OrphanThresholdMinutes = 10, BatchSize = 200,
            ConsumeTerminalScored = true
            // ConsumeFromUtc = null
        });
        var sau = DateTime.UtcNow;

        using (provider)
        {
            var mark = ConsumeMark(r);
            Assert.InRange(mark, truoc, sau);   // chặn cả MinValue lẫn MaxValue lẫn mọi hằng "hợp lý" khác
            Assert.Equal(DateTimeKind.Utc, mark.Kind);
        }
    }

    // Mốc cấu hình TƯỜNG MINH phải được tôn trọng nguyên vẹn (ops muốn quét cả tồn đọng thì đặt mốc sớm).
    [Fact]
    public async Task R1_MocCauHinhTuongMinh_DuocTonTrong()
    {
        using var tdb = new PaymentTestDb();
        var moc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var (r, provider) = BuildWith(tdb, EmptyClient(), new OrphanReconcileSettings
        {
            Enabled = true, ScanIntervalSeconds = 120, OrphanThresholdMinutes = 10, BatchSize = 200,
            ConsumeTerminalScored = true,
            ConsumeFromUtc = moc
        });

        using (provider)
            Assert.Equal(moc, ConsumeMark(r));
    }

    // Trộn nhiều trạng thái trong CÙNG 1 lô: mỗi chỗ giữ đi đúng nhánh của nó, không lây chéo.
    [Fact]
    public async Task R1_LoTronNhieuTrangThai_MoiCaiDungNhanh()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        SeedAccount(tdb, OwnerType.User, owner, reserved: 4, remaining: 6);
        var scored = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        var abandoned = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        var live = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        var orphan = SeedReservation(tdb, OwnerType.User, owner, ReservationStatus.Reserved, Old);
        await tdb.Db.SaveChangesAsync();

        var (r, provider) = Build(tdb, Client(
            (scored, "Scored"), (abandoned, "SessionAbandoned"), (live, "InProgress")));
        using (provider)
            await ScanOnce(r);

        using var read = tdb.NewContext();
        var byId = await read.CreditReservations.ToDictionaryAsync(x => x.SessionId, x => x.Status);
        Assert.Equal(ReservationStatus.Consumed, byId[scored]);
        Assert.Equal(ReservationStatus.Released, byId[abandoned]);
        Assert.Equal(ReservationStatus.Reserved, byId[live]);
        Assert.Equal(ReservationStatus.Released, byId[orphan]);   // không có trong existingIds

        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(1, acc.ReservedCredits);     // 4 − 3 (1 consume + 2 release)
        Assert.Equal(8, acc.RemainingCredits);    // 6 + 2 (chỉ release hoàn, consume KHÔNG)
    }
}
