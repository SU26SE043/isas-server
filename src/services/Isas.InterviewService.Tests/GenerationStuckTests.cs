using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Zombie `GeneratingQuestions` (đo prod 2026-09-15: 2 buổi, reservation đã Released nhưng session kẹt mãi).
/// Hai lớp vá:
/// (1) PracticeService: lỗi sinh câu hỏi là HUỶ request (client đóng tab / AI timeout) thì dòng `Failed`
///     phải vẫn xuống DB — bản cũ ghi bằng `ct` đã cancel nên SaveChanges ném ngay, Failed không bao giờ ghi.
/// (2) SessionAbandonSweeper: buổi kẹt GeneratingQuestions quá `Scoring:GenerationStuckMinutes` → Failed +
///     outbox SessionAbandoned(generation_failed) (Payment release idempotent).
/// </summary>
public class GenerationStuckTests
{
    // ───────────────────────── (1) PracticeService ─────────────────────────

    private static PracticeService BuildService(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen,
        out Mock<ISessionScoringNotifier> notifier, out Mock<ICreditReservationClient> reservation)
    {
        notifier = new Mock<ISessionScoringNotifier>();
        // Notifier THẬT ghi outbox vào cùng DbContext — ở đây dùng mock nhưng ghi lại token nhận được để
        // khẳng định đường ghi KHÔNG dùng token đã huỷ (đó là chính cái bug).
        notifier
            .Setup(n => n.EnqueueSessionAbandonedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, notifier.Object,
            reservation.Object, NullLogger<PracticeService>.Instance);
    }

    // Client huỷ request ĐÚNG LÚC AI đang sinh (ct cancel + OperationCanceledException) → session vẫn phải
    // là Failed trong DB (không phải GeneratingQuestions), outbox generation_failed vẫn được ghi bằng token
    // KHÔNG huỷ, và credit vẫn được release.
    [Fact]
    public async Task Create_RequestBiHuyGiuaLucSinh_SessionVanFailed_KhongZombie()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();                                   // client rời đi trong lúc chờ AI
                throw new OperationCanceledException(cts.Token);
            });

        var svc = BuildService(t, gen, out var notifier, out var reservation);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.CreateSessionAsync(candidate, req, cts.Token));

        using var read = t.NewContext();
        var s = await read.PracticeSessions.AsNoTracking().SingleAsync(x => x.CandidateId == candidate);
        Assert.Equal(SessionStatus.Failed, s.Status);   // bản cũ: GeneratingQuestions vĩnh viễn

        notifier.Verify(n => n.EnqueueSessionAbandonedAsync(
            s.Id, "generation_failed", It.Is<CancellationToken>(c => !c.IsCancellationRequested)), Times.Once);
        reservation.Verify(r => r.ReleaseAsync(s.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ───────────────────────── (2) Sweeper ─────────────────────────

    private const string AbandonedType = "session.abandoned";

    private static async Task ScanOnce(SessionAbandonSweeper s)
    {
        var mi = typeof(SessionAbandonSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(s, new object[] { CancellationToken.None })!;
    }

    private static SessionAbandonSweeper BuildSweeper(TestDb t, int generationStuckMinutes = 15)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();
        return new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ScoringOptions { GenerationStuckMinutes = generationStuckMinutes }),
            NullLogger<SessionAbandonSweeper>.Instance);
    }

    [Fact]
    public async Task Sweeper_GeneratingQuestionsQuaTran_ChotFailed_GhiOutboxGenerationFailed()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var stuck = TestDb.Session(candidate, SessionStatus.GeneratingQuestions,
            createdAt: DateTime.UtcNow.AddMinutes(-40));
        t.Db.Add(stuck);
        await t.Db.SaveChangesAsync();

        await ScanOnce(BuildSweeper(t));

        using var read = t.NewContext();
        var s = await read.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == stuck.Id);
        Assert.Equal(SessionStatus.Failed, s.Status);

        Assert.Equal(1, TestDb.OutboxCount(read, stuck.Id, AbandonedType));
        var evt = TestDb.AbandonedOutbox(read, stuck.Id);
        Assert.NotNull(evt);
        Assert.Equal("generation_failed", evt!.Reason);   // = BK12 → Payment release bất kể PONR1
        Assert.Equal(candidate, evt.CandidateId);
        Assert.Null(evt.CampaignId);
    }

    // Buổi MỚI tạo (đang sinh thật) không được đụng — mốc CreatedAt đứng yên, trần 15' > mọi timeout AI.
    [Fact]
    public async Task Sweeper_GeneratingQuestionsMoi_KhongDung()
    {
        using var t = new TestDb();
        var fresh = TestDb.Session(Guid.NewGuid(), SessionStatus.GeneratingQuestions,
            createdAt: DateTime.UtcNow.AddMinutes(-2));
        t.Db.Add(fresh);
        await t.Db.SaveChangesAsync();

        await ScanOnce(BuildSweeper(t));

        using var read = t.NewContext();
        Assert.Equal(SessionStatus.GeneratingQuestions,
            (await read.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == fresh.Id)).Status);
        Assert.Equal(0, TestDb.OutboxCount(read, fresh.Id, AbandonedType));
    }

    // Quét lần 2 không enqueue lần 2 (đã Failed → lọc ngoài; guard WHERE status cũng chặn).
    [Fact]
    public async Task Sweeper_QuetLai_KhongHoanCreditDoi()
    {
        using var t = new TestDb();
        var stuck = TestDb.Session(Guid.NewGuid(), SessionStatus.GeneratingQuestions,
            createdAt: DateTime.UtcNow.AddMinutes(-40));
        t.Db.Add(stuck);
        await t.Db.SaveChangesAsync();

        var sweeper = BuildSweeper(t);
        await ScanOnce(sweeper);
        await ScanOnce(sweeper);

        using var read = t.NewContext();
        Assert.Equal(1, TestDb.OutboxCount(read, stuck.Id, AbandonedType));
    }

    // ĐUA: request sinh câu hỏi hoàn tất (→ Ready) ĐÚNG giữa lúc sweeper đã chọn buổi và sắp UPDATE →
    // guard WHERE status=GeneratingQuestions 0 row → KHÔNG lật Ready thành Failed, KHÔNG enqueue hoàn credit
    // (hoàn credit cho buổi đang chạy = buổi miễn phí). Chen bằng DbCommandInterceptor ngay trước UPDATE
    // (mẫu DB21). SQLite chia 1 connection nên câu chen chạy trong cùng transaction — rollback sẽ hoàn cả
    // câu chen, vì thế phép quyết định là "không outbox + không Failed", không phải "vẫn Ready".
    [Fact]
    public async Task Sweeper_BuoiVuaReadyGiuaChung_GuardAtomic_KhongLatFailed()
    {
        using var t = new TestDb();
        var stuck = TestDb.Session(Guid.NewGuid(), SessionStatus.GeneratingQuestions,
            createdAt: DateTime.UtcNow.AddMinutes(-40));
        t.Db.Add(stuck);
        await t.Db.SaveChangesAsync();

        var flip = new FlipToReadyBeforeUpdateInterceptor();
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o
            .UseSqlite(t.Connection).UseSnakeCaseNamingConvention().AddInterceptors(flip));
        var sweeper = new SessionAbandonSweeper(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ScoringOptions { GenerationStuckMinutes = 15 }),
            NullLogger<SessionAbandonSweeper>.Instance);

        await ScanOnce(sweeper);

        Assert.True(flip.Fired, "interceptor phải chen được — nếu không phép đo này vô nghĩa");
        using var read = t.NewContext();
        Assert.NotEqual(SessionStatus.Failed,
            (await read.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == stuck.Id)).Status);
        Assert.Equal(0, TestDb.OutboxCount(read, stuck.Id, AbandonedType));
    }

    private sealed class FlipToReadyBeforeUpdateInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.StartsWith("UPDATE \"practice_sessions\"", StringComparison.Ordinal))
            {
                Fired = true;
                using var chen = command.Connection!.CreateCommand();
                chen.Transaction = command.Transaction;
                // Không WHERE theo id: EF lưu Guid vào SQLite dạng TEXT chữ HOA, so bằng chuỗi thường là trượt.
                chen.CommandText = "UPDATE practice_sessions SET status = 'Ready' WHERE status = 'GeneratingQuestions'";
                await chen.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    // 0 = tắt (giữ hành vi cũ) — kill-switch phải thật sự tắt được.
    [Fact]
    public async Task Sweeper_Tran0_Tat_KhongQuet()
    {
        using var t = new TestDb();
        var stuck = TestDb.Session(Guid.NewGuid(), SessionStatus.GeneratingQuestions,
            createdAt: DateTime.UtcNow.AddHours(-5));
        t.Db.Add(stuck);
        await t.Db.SaveChangesAsync();

        await ScanOnce(BuildSweeper(t, generationStuckMinutes: 0));

        using var read = t.NewContext();
        Assert.Equal(SessionStatus.GeneratingQuestions,
            (await read.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == stuck.Id)).Status);
    }

    // Mặc định trong code phải là 15 — thứ có hiệu lực trên deploy khi env/appsettings không khai (bài học
    // `multi_voice`: mọi test đều set tường minh ⇒ không ai phủ giá trị mặc định).
    [Fact]
    public void GenerationStuckMinutes_MacDinh15()
    {
        Assert.Equal(15, new ScoringOptions().GenerationStuckMinutes);
    }
}
