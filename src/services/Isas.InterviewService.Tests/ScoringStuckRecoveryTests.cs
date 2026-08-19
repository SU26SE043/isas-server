using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
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
/// Buổi kẹt <c>Scoring</c> VĨNH VIỄN — sự cố prod 2026-08-15 (session <c>39834dbb</c>).
/// </summary>
/// <remarks>
/// Chuỗi nhân quả đã đo được, mỗi mắt là một test dưới đây:
///   1. buổi tier <c>pro</c> ⇒ N = 3 attempt (E10); AnswerService publish attempt 1/2/3;
///   2. một message chết (rơi DLQ) ⇒ DB chỉ có attempt 2;
///   3. <c>StuckAnswerRepublisher</c> dựng job KHÔNG set <c>AttemptNo</c> ⇒ nhận mặc định 1 ⇒ đẩy
///      lại attempt 1 MÃI MÃI ⇒ số attempt distinct không bao giờ tới 3;
///   4. không sweeper nào phủ <c>Scoring</c> ⇒ buổi nằm đó vĩnh viễn, credit treo ở
///      <c>Reserved</c> (<c>OrphanReservationReconciler</c> chỉ xử session TERMINAL).
///
/// Đo thật lúc phát hiện: answer có đúng attempt {1, 2}, cần 3, và mỗi 15 phút lại tốn thêm một
/// lượt Gemini cho một answer đã hết cứu.
/// </remarks>
public class ScoringStuckRecoveryTests
{
    private static async Task ScanOnce(StuckAnswerRepublisher r)
    {
        var mi = typeof(StuckAnswerRepublisher)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    private static (StuckAnswerRepublisher r, Mock<IScoringJobPublisher> pub) BuildRepublisher(
        TestDb t, ScoringOptions? scoring = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o =>
            o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var pub = new Mock<IScoringJobPublisher>();
        var r = new StuckAnswerRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            Options.Create(new RepublisherSettings { BatchSize = 200 }),
            Options.Create(scoring ?? new ScoringOptions()),
            NullLogger<StuckAnswerRepublisher>.Instance);
        return (r, pub);
    }

    /// <summary>Buổi B2C đã submit, đang chờ chấm với N attempt (tier resolve → SelfConsistencyN).</summary>
    private static async Task<(PracticeSession s, PracticeQuestion q, PracticeAnswer a, RubricCriterion c)>
        SeedStuckAsync(TestDb t, int selfConsistencyN, DateTime createdAt, DateTime? lastPublished)
    {
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        session.SelfConsistencyN = selfConsistencyN;
        session.EntitlementSource = "resolved";   // ≠ "legacy" ⇒ dùng con số của buổi (T7)
        session.CompletedAt = createdAt;
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, createdAt, lastPublished);
        var c = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, a, c);
        await t.Db.SaveChangesAsync();
        return (session, q, a, c);
    }

    private static AnswerScore Score(Guid answerId, Guid criterionId, int attemptNo, decimal score = 3m)
        => new()
        {
            Id = Guid.NewGuid(),
            AnswerId = answerId,
            CriterionId = criterionId,
            Score = score,
            Reasoning = "Ứng viên nêu được ví dụ cụ thể về transaction.",
            AttemptNo = attemptNo,
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        };

    // ── Republisher: bù ĐÚNG attempt còn thiếu ───────────────────────────────
    [Fact]
    public async Task Republisher_BuDungAttemptConThieu_KhongDayLaiAttemptDaCo()
    {
        using var t = new TestDb();
        var (_, _, a, c) = await SeedStuckAsync(
            t, selfConsistencyN: 3,
            // 2026-08-20 — mốc cũ (-20'/-16') nay NẰM NGOÀI trần bỏ cuộc `Scoring:GiveUpAfterMinutes`
            // (60' → 20'), nên republisher thôi đẩy và các assert dưới đo nhầm nhánh. -10'/-6' giữ đúng
            // ý định gốc: đã quá `Republisher:ScoringLostMinutes` (3') mà vẫn trong trần bỏ cuộc.
            createdAt: DateTime.UtcNow.AddMinutes(-10),
            lastPublished: DateTime.UtcNow.AddMinutes(-6));
        t.Db.Add(Score(a.Id, c.Id, attemptNo: 2));   // đúng hiện trạng prod: chỉ có attempt 2
        await t.Db.SaveChangesAsync();

        var published = new List<ScoringJob>();
        var (r, pub) = BuildRepublisher(t);
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published.Add(j))
            .Returns(Task.CompletedTask);

        await ScanOnce(r);

        // Đây là toàn bộ lý do buổi kẹt: trước bản vá chỗ này đẩy MỘT job attempt 1 (mặc định DTO).
        Assert.Equal(new[] { 1, 3 }, published.Select(j => j.AttemptNo).OrderBy(x => x).ToArray());
        Assert.DoesNotContain(published, j => j.AttemptNo == 2);
    }

    [Fact]
    public async Task Republisher_GiuHopDongNhietDo_Attempt1Bang0_ConLaiDaoDong()
    {
        using var t = new TestDb();
        await SeedStuckAsync(t, selfConsistencyN: 3,
            // 2026-08-20 — mốc cũ (-20'/-16') nay NẰM NGOÀI trần bỏ cuộc `Scoring:GiveUpAfterMinutes`
            // (60' → 20'), nên republisher thôi đẩy và các assert dưới đo nhầm nhánh. -10'/-6' giữ đúng
            // ý định gốc: đã quá `Republisher:ScoringLostMinutes` (3') mà vẫn trong trần bỏ cuộc.
            createdAt: DateTime.UtcNow.AddMinutes(-10),
            lastPublished: DateTime.UtcNow.AddMinutes(-6));

        var published = new List<ScoringJob>();
        var (r, pub) = BuildRepublisher(t, new ScoringOptions { SelfConsistencyTemperature = 0.4 });
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published.Add(j))
            .Returns(Task.CompletedTask);

        await ScanOnce(r);

        // Bù attempt 2 bằng temp=0 sẽ làm spread (E10) giả bằng 0 — mất luôn ý nghĩa self-consistency.
        Assert.Equal(0d, published.Single(j => j.AttemptNo == 1).Temperature);
        Assert.All(published.Where(j => j.AttemptNo > 1), j => Assert.Equal(0.4, j.Temperature));
    }

    [Fact]
    public async Task Republisher_DuAttempt_KhongDayLaiNua()
    {
        using var t = new TestDb();
        var (_, _, a, c) = await SeedStuckAsync(
            t, selfConsistencyN: 2,
            // Trong trần bỏ cuộc (20') — để -20' thì test xanh vì nhánh SAI (quá trần), chứ không
            // phải vì "đã đủ attempt" như tên test nói.
            createdAt: DateTime.UtcNow.AddMinutes(-10),
            lastPublished: DateTime.UtcNow.AddMinutes(-6));
        t.Db.AddRange(Score(a.Id, c.Id, 1), Score(a.Id, c.Id, 2));
        await t.Db.SaveChangesAsync();

        var (r, pub) = BuildRepublisher(t);
        await ScanOnce(r);

        // Đẩy thêm KHÔNG cứu được gì (bước chốt mới là chỗ hỏng) — chỉ đốt token.
        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Republisher_QuaTranBoCuoc_ThoiDayLai()
    {
        using var t = new TestDb();
        await SeedStuckAsync(t, selfConsistencyN: 3,
            createdAt: DateTime.UtcNow.AddMinutes(-90),   // > GiveUpAfterMinutes mặc định 20 (trước 2026-08-20: 60)
            lastPublished: DateTime.UtcNow.AddMinutes(-16));

        var (r, pub) = BuildRepublisher(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Republisher_TranBoCuocNeoTheoCreatedAt_KhongPhaiMocPublish()
    {
        using var t = new TestDb();
        // Mốc publish vừa được chính vòng đẩy-lại dời về gần `now`. Neo vào nó thì trần KHÔNG BAO
        // GIỜ tới — đúng bẫy đã cắn StuckScreeningRepublisher (C14).
        await SeedStuckAsync(t, selfConsistencyN: 3,
            createdAt: DateTime.UtcNow.AddMinutes(-90),
            lastPublished: DateTime.UtcNow.AddMinutes(-16));

        var (r, pub) = BuildRepublisher(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Chốt sổ cưỡng bức: buổi không được phép kẹt vĩnh viễn ────────────────
    private static AnswerService BuildAnswerService(TestDb t)
        => new(
            t.Db,
            Mock.Of<IStorageService>(),
            Mock.Of<IScoringJobPublisher>(),
            Mock.Of<ISessionScoringNotifier>(),
            Options.Create(new ScoringOptions()),
            NullLogger<AnswerService>.Instance);

    [Fact]
    public async Task ChotSoCuongBuc_ConThieuAttempt_VanChotBangDiemDaCo_VaGanNeedsReview()
    {
        using var t = new TestDb();
        var (session, _, a, c) = await SeedStuckAsync(
            t, selfConsistencyN: 3,
            createdAt: DateTime.UtcNow.AddMinutes(-90),
            lastPublished: DateTime.UtcNow.AddMinutes(-70));
        t.Db.AddRange(Score(a.Id, c.Id, 1), Score(a.Id, c.Id, 2));
        await t.Db.SaveChangesAsync();

        await BuildAnswerService(t).FinalizeStuckSessionAsync(session.Id);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id);
        // Người luyện ĐÃ TRẢ 1 credit (PAY-13): vứt 2 attempt đang có là phạt oan họ vì một
        // message chết. Median trên 2 mẫu vẫn dùng được — nhưng phải NÓI RA là mỏng hơn thiết kế.
        Assert.Equal(AnswerStatus.Scored, saved.Status);
        Assert.True(saved.NeedsReview);
        Assert.Equal(SessionStatus.Scored,
            (await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id)).Status);
    }

    [Fact]
    public async Task ChotSoCuongBuc_KhongCoAttemptNao_ThanhSkipped_VaBuoiAbandoned()
    {
        using var t = new TestDb();
        var (session, _, a, _) = await SeedStuckAsync(
            t, selfConsistencyN: 3,
            createdAt: DateTime.UtcNow.AddMinutes(-90),
            lastPublished: DateTime.UtcNow.AddMinutes(-70));

        var notifier = new Mock<ISessionScoringNotifier>();
        var svc = new AnswerService(t.Db, Mock.Of<IStorageService>(), Mock.Of<IScoringJobPublisher>(),
            notifier.Object, Options.Create(new ScoringOptions()), NullLogger<AnswerService>.Instance);

        await svc.FinalizeStuckSessionAsync(session.Id);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id);
        Assert.Equal(AnswerStatus.Skipped, saved.Status);

        // PAY-13: không answer nào Scored ⇒ SessionAbandoned ⇒ Payment RELEASE credit (không trừ
        // tiền buổi hỏng). Đây là vế TIỀN của bản vá — trước đây credit treo `Reserved` vĩnh viễn.
        Assert.Equal(SessionStatus.SessionAbandoned,
            (await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id)).Status);
        notifier.Verify(n => n.EnqueueSessionAbandonedAsync(
            session.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChotSoCuongBuc_BuoiChuaSubmit_KhongDung()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring,
            DateTime.UtcNow.AddMinutes(-90), null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var touched = await BuildAnswerService(t).FinalizeStuckSessionAsync(session.Id);

        // Buổi CÒN ĐANG LÀM không phải việc của đường bỏ cuộc — chốt sổ ở đây là cướp bài của
        // người đang thi.
        Assert.False(touched);
        Assert.Equal(AnswerStatus.Scoring,
            (await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id)).Status);
    }

    // ⚠ Hai test dưới CỐ Ý gọi `ScanOnceAsync` (điểm vào thật của vòng quét) chứ không gọi thẳng
    // `ScanStuckScoringAsync`. Lượt mutation đầu cho thấy vì sao: gỡ lời gọi ra khỏi `ScanOnceAsync`
    // vẫn XANH 757/757 khi test đi cửa sau — tức là chỗ ĐẤU DÂY hoàn toàn không được phủ, và
    // production sẽ không bao giờ chạy pass này mà không ai kêu. Đúng lớp lỗ đã gặp ở Q10.
    [Fact]
    public async Task Sweeper_QuetBuoiKetScoring_GoiChotSo()
    {
        using var t = new TestDb();
        var (session, _, _, _) = await SeedStuckAsync(
            t, selfConsistencyN: 3,
            createdAt: DateTime.UtcNow.AddMinutes(-90),
            lastPublished: DateTime.UtcNow.AddMinutes(-70));

        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o =>
            o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var answers = new Mock<IAnswerService>();
        services.AddScoped(_ => answers.Object);
        var provider = services.BuildServiceProvider();

        var sweeper = new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ScoringOptions()),
            NullLogger<SessionAbandonSweeper>.Instance);
        var mi = typeof(SessionAbandonSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(sweeper, new object[] { CancellationToken.None })!;

        answers.Verify(x => x.FinalizeStuckSessionAsync(session.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Sweeper_BuoiScoringChuaQuaHan_KhongDung()
    {
        using var t = new TestDb();
        await SeedStuckAsync(t, selfConsistencyN: 3,
            createdAt: DateTime.UtcNow.AddMinutes(-5),
            lastPublished: DateTime.UtcNow.AddMinutes(-3));

        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o =>
            o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var answers = new Mock<IAnswerService>();
        services.AddScoped(_ => answers.Object);
        var provider = services.BuildServiceProvider();

        var sweeper = new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ScoringOptions()),
            NullLogger<SessionAbandonSweeper>.Instance);
        var mi = typeof(SessionAbandonSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(sweeper, new object[] { CancellationToken.None })!;

        // Worker chấm chậm KHÔNG được coi là kẹt — chốt sổ sớm là vứt bài đang chấm dở.
        answers.Verify(x => x.FinalizeStuckSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Nhãn im lặng: Skipped, không phải Failed ─────────────────────────────
    [Fact]
    public async Task MarkFailed_NoSpeech_ThanhSkipped_KhongPhaiFailed()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        await BuildAnswerService(t).MarkFailedAsync(a.Id, "Bản ghi không có tiếng nói (VAD)", noSpeech: true);

        Assert.Equal(AnswerStatus.Skipped,
            (await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id)).Status);
    }

    // ── Đường THÍCH ỨNG: im lặng bị chặn NGAY, không đi tới bộ chấm ──────────
    [Fact]
    public async Task Adaptive_NoSpeech_AnswerSkipped_VaKhongPublishJobCham()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        session.AdaptiveEnabled = true;
        session.MaxQuestions = 10;
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var decider = new Mock<IAiServiceInterviewDecider>();
        decider.Setup(x => x.DecideNextAsync(
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecideNextResult(
                "end", null, null, "Bản chép bị từ chối: no_speech", RejectReason: "no_speech"));

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/x.m4a");
        var publisher = new Mock<IScoringJobPublisher>();

        var svc = new AnswerService(t.Db, storage.Object, publisher.Object,
            Mock.Of<ISessionScoringNotifier>(), Options.Create(new ScoringOptions()),
            NullLogger<AnswerService>.Instance, decider.Object);

        using var audio = new MemoryStream(new byte[] { 1 });
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/m4a", 8);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.SessionId == session.Id);
        Assert.Equal(AnswerStatus.Skipped, saved.Status);

        // Vế đắt nhất: KHÔNG publish ⇒ không tốn lượt Gemini để chấm một câu do máy bịa ra.
        // Trên prod 2026-08-15, đúng ca này đã sinh ra 5 dòng điểm 0.0 kèm reasoning trích nguyên
        // câu quảng cáo "Ghiền Mì Gõ" mà Whisper ảo giác từ 8 giây im lặng.
        publisher.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Adaptive_CoTiengNoi_VanPublishJobCham()
    {
        // Đối chứng: bản vá KHÔNG được chặn nhầm câu trả lời thật. Thiếu test này thì "chặn tất"
        // cũng làm test trên xanh — mà như vậy là không ai được chấm nữa.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        session.AdaptiveEnabled = true;
        session.MaxQuestions = 10;
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var decider = new Mock<IAiServiceInterviewDecider>();
        decider.Setup(x => x.DecideNextAsync(
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecideNextResult("end", null, "tôi dùng index để tối ưu truy vấn", null));

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/x.m4a");
        var publisher = new Mock<IScoringJobPublisher>();

        var svc = new AnswerService(t.Db, storage.Object, publisher.Object,
            Mock.Of<ISessionScoringNotifier>(), Options.Create(new ScoringOptions()),
            NullLogger<AnswerService>.Instance, decider.Object);

        using var audio = new MemoryStream(new byte[] { 1 });
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/m4a", 30);

        publisher.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task MarkFailed_MacDinh_GiuNguyenFailed()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        // Worker bản CŨ không gửi cờ ⇒ hành vi y hệt trước bản vá.
        await BuildAnswerService(t).MarkFailedAsync(a.Id, "LLM output không hợp lệ");

        Assert.Equal(AnswerStatus.Failed,
            (await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id)).Status);
    }
}
