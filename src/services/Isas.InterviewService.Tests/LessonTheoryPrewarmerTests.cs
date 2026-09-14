using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Prewarm lý thuyết bài học (<see cref="LessonPrewarmQueue"/> + <see cref="LessonTheoryPrewarmer"/>):
/// bài 1 ngay sau tạo roadmap, bài KẾ khi mở bài — sinh nền qua single-flight, không cascade, không retry.
/// Mỗi tính chất một mutation phải ĐỎ (ghi ở từng test).
/// Harness: SQLite shared-cache đặt tên (hai scope, hai DbContext) như RoadmapLessonSingleFlightTests.
/// </summary>
public class LessonTheoryPrewarmerTests : IDisposable
{
    private readonly SqliteConnection _keeper;
    private readonly string _connString;
    private readonly ServiceProvider _provider;
    private readonly Mock<IAiServiceRoadmapGenerator> _gen = new(MockBehavior.Strict);
    private readonly Mock<IPracticeService> _practice = new(MockBehavior.Strict);
    private const string Theory = "# Bài\n\n## Mục 1\nNội dung đủ dùng.";

    public LessonTheoryPrewarmerTests()
    {
        _connString = $"DataSource=pw-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(_connString);
        _keeper.Open();
        using (var db = NewDb()) db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(_connString).UseSnakeCaseNamingConvention());
        services.AddSingleton(_gen.Object);
        services.AddSingleton(_practice.Object);
        services.AddSingleton(Options.Create(new LessonPrewarmOptions { Enabled = true, QueueCapacity = 8 }));
        services.AddScoped<IRoadmapLessonService, RoadmapLessonService>();
        services.AddSingleton<LessonTheorySingleFlight>();
        services.AddSingleton<LessonPrewarmQueue>();
        services.AddSingleton<LessonTheoryPrewarmer>();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _keeper.Dispose();
    }

    private InterviewDbContext NewDb() => new(new DbContextOptionsBuilder<InterviewDbContext>()
        .UseSqlite(_connString).UseSnakeCaseNamingConvention().Options);

    private LessonPrewarmQueue Queue => _provider.GetRequiredService<LessonPrewarmQueue>();
    private LessonTheoryPrewarmer Prewarmer => _provider.GetRequiredService<LessonTheoryPrewarmer>();

    /// <summary>2 chặng: M1 = L1, L2 · M2 = L3. Theory tuỳ chọn cho từng bài.</summary>
    private (Guid cand, Guid rid, Guid l1, Guid l2, Guid l3) Seed(string? t1 = null, string? t2 = null, string? t3 = null)
    {
        using var db = NewDb();
        var cand = Guid.NewGuid();
        var l1 = new RoadmapLesson { Id = Guid.NewGuid(), OrderNo = 1, Title = "L1", Status = LessonStatus.Theory, TheoryContent = t1 };
        var l2 = new RoadmapLesson { Id = Guid.NewGuid(), OrderNo = 2, Title = "L2", Status = LessonStatus.Theory, TheoryContent = t2 };
        var l3 = new RoadmapLesson { Id = Guid.NewGuid(), OrderNo = 1, Title = "L3", Status = LessonStatus.Theory, TheoryContent = t3 };
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = cand, JobCategory = JobCategory.BE, Level = RoadmapLevel.Junior,
            Status = RoadmapStatus.Active, CreatedAt = DateTime.UtcNow,
            Milestones =
            [
                new RoadmapMilestone { Id = Guid.NewGuid(), OrderNo = 1, Title = "M1", FocusCriteria = ["Clarity"], Status = MilestoneStatus.Pending, Lessons = [l1, l2] },
                new RoadmapMilestone { Id = Guid.NewGuid(), OrderNo = 2, Title = "M2", FocusCriteria = ["Depth"], Status = MilestoneStatus.Pending, Lessons = [l3] },
            ]
        };
        db.Roadmaps.Add(roadmap);
        db.SaveChanges();
        return (cand, roadmap.Id, l1.Id, l2.Id, l3.Id);
    }

    private (TaskCompletionSource<LessonTheoryResult> gate, List<string> titles) GateAi()
    {
        var gate = new TaskCompletionSource<LessonTheoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var titles = new List<string>();
        _gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(),
                It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .Returns((string _, string _, string title, IReadOnlyList<string> _, IReadOnlyList<string>? _,
                      IReadOnlyList<GroundingChunk>? _, IReadOnlyList<CriterionEvidence>? _, RoadmapMode _,
                      CancellationToken _, IReadOnlyList<RoadmapMistake>? _) =>
            {
                lock (titles) titles.Add(title);
                return gate.Task;
            });
        return (gate, titles);
    }

    private void VerifyAiCalls(Times times) => _gen.Verify(g => g.GenerateLessonTheoryAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(),
        It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(),
        It.IsAny<IReadOnlyList<RoadmapMistake>?>()), times);

    private async Task<LessonResponse> Open(Guid cand, Guid rid, Guid lid)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRoadmapLessonService>().OpenLessonAsync(cand, rid, lid);
    }

    private List<Guid> Drain()
    {
        var ids = new List<Guid>();
        while (Queue.Reader.TryRead(out var id)) { ids.Add(id); Queue.MarkDequeued(id); }
        return ids;
    }

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (var i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "điều kiện chờ không xảy ra");
    }

    // ── Enqueue khi tạo roadmap ─────────────────────────────────────────────
    [Fact]
    public async Task Create_SauSaveChanges_EnqueueBaiDauTien()
    {
        // Mutation: bỏ TryEnqueue trong CreateAsync → ĐỎ; enqueue bài cuối thay bài đầu → ĐỎ.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<RoadmapWeakness>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(), It.IsAny<RoadmapMode>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .ReturnsAsync(new RoadmapGenAiResult(new List<GeneratedMilestone>
            {
                new("M1", new List<string> { "Clarity" }, new List<GeneratedLesson> { new("L1"), new("L2") }),
                new("M2", new List<string> { "Depth" }, new List<GeneratedLesson> { new("L3") })
            }));
        var queue = new LessonPrewarmQueue(Options.Create(new LessonPrewarmOptions { Enabled = true }));
        var svc = new RoadmapService(t.Db, new Mock<IStorageService>().Object, gen.Object,
            NullLogger<RoadmapService>.Instance, prewarm: queue);
        var sid = TestSeed.ScoredSessionWithAnswers(t, candidate, JobCategory.FE, seedContentMistakes: true, ("Clarity", 40m, true));

        var res = await svc.CreateAsync(candidate,
            new CreateRoadmapRequest(JobCategory.FE, RoadmapLevel.Fresher, null, SessionIds: [sid]), default);

        var first = res.Milestones.OrderBy(m => m.OrderNo).First().Lessons.OrderBy(l => l.OrderNo).First();
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.Equal(first.Id, queued);
        Assert.False(queue.Reader.TryRead(out _));
    }

    // ── Enqueue bài kế khi mở bài ───────────────────────────────────────────
    [Fact]
    public async Task OpenLesson_EnqueueBaiKeCungChang()
    {
        var (cand, rid, l1, l2, _) = Seed(t1: Theory);
        await Open(cand, rid, l1);
        Assert.Equal([l2], Drain());
        VerifyAiCalls(Times.Never());   // L1 đã có bài → không gọi AI; prewarm chỉ XẾP HÀNG, chưa sinh
    }

    [Fact]
    public async Task OpenLesson_BaiCuoiChang_EnqueueBaiDauChangKe()
    {
        var (cand, rid, _, l2, l3) = Seed(t2: Theory);
        await Open(cand, rid, l2);
        Assert.Equal([l3], Drain());
    }

    [Fact]
    public async Task OpenLesson_BaiCuoiCung_KhongEnqueue()
    {
        var (cand, rid, _, _, l3) = Seed(t3: Theory);
        await Open(cand, rid, l3);
        Assert.Empty(Drain());
    }

    [Fact]
    public async Task OpenLesson_BaiKeDaCoBai_KhongEnqueue()
    {
        // Mutation: bỏ vị ngữ Usable trong EnqueueNextLessonPrewarmAsync → ĐỎ (xếp hàng bài đã có).
        var (cand, rid, l1, _, _) = Seed(t1: Theory, t2: Theory);
        await Open(cand, rid, l1);
        Assert.Empty(Drain());
    }

    [Fact]
    public async Task OpenLesson_BaiKeChiCoMotDongTieuDe_VanEnqueue()
    {
        // Vị ngữ usable phải khớp HasUsableTheory: bài một dòng tiêu đề là "chưa dùng được" → prewarm sinh lại.
        var (cand, rid, l1, l2, _) = Seed(t1: Theory, t2: "# Chỉ có tiêu đề");
        await Open(cand, rid, l1);
        Assert.Equal([l2], Drain());
    }

    [Fact]
    public async Task OpenLesson_VuaSinhXong_CungEnqueueBaiKe()
    {
        // Đường vừa sinh (không chỉ đường đọc lại) cũng phải xếp bài kế.
        var (cand, rid, l1, l2, _) = Seed();
        var (gate, _) = GateAi();
        gate.SetResult(new LessonTheoryResult(Theory, []));
        await Open(cand, rid, l1);
        Assert.Equal([l2], Drain());
    }

    // ── Queue ───────────────────────────────────────────────────────────────
    [Fact]
    public void Queue_TrungLap_ChiMotPhanTu()
    {
        // Mutation: bỏ set `_queued` → ĐỎ.
        var q = new LessonPrewarmQueue(Options.Create(new LessonPrewarmOptions { Enabled = true }));
        var id = Guid.NewGuid();
        Assert.True(q.TryEnqueue(id));
        Assert.False(q.TryEnqueue(id));
        Assert.Equal(1, q.PendingCount);
        Assert.True(q.Reader.TryRead(out _));
        Assert.False(q.Reader.TryRead(out _));
        // Sau khi rút ra (MarkDequeued) mới được xếp lại.
        q.MarkDequeued(id);
        Assert.True(q.TryEnqueue(id));
    }

    [Fact]
    public void Queue_Disabled_TuChoi()
    {
        var q = new LessonPrewarmQueue(Options.Create(new LessonPrewarmOptions { Enabled = false }));
        Assert.False(q.Enabled);
        Assert.False(q.TryEnqueue(Guid.NewGuid()));
        Assert.False(q.Reader.TryRead(out _));
    }

    [Fact]
    public void Queue_Day_BoKhongChan()
    {
        var q = new LessonPrewarmQueue(Options.Create(new LessonPrewarmOptions { Enabled = true, QueueCapacity = 2 }));
        Assert.True(q.TryEnqueue(Guid.NewGuid()));
        Assert.True(q.TryEnqueue(Guid.NewGuid()));
        Assert.False(q.TryEnqueue(Guid.NewGuid()));   // đầy → bỏ, không ném, không chờ
        Assert.Equal(2, q.PendingCount);
    }

    // ── Prewarmer ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Prewarmer_BaiDaCoBai_KhongGoiAi()
    {
        // Mutation: bỏ early-return trong GenerateAndPersistAsync → Strict mock ném → ĐỎ.
        var (_, _, l1, _, _) = Seed(t1: Theory);
        Assert.True(Queue.TryEnqueue(l1));
        await Prewarmer.ProcessQueuedAsync(default);
        VerifyAiCalls(Times.Never());
        Assert.Equal(0, Queue.PendingCount);
    }

    [Fact]
    public async Task Prewarmer_SinhVaLuu_KhongDungCreditKhongDungPractice()
    {
        var (_, _, l1, _, _) = Seed();
        var (gate, _) = GateAi();
        gate.SetResult(new LessonTheoryResult(Theory, []));
        Assert.True(Queue.TryEnqueue(l1));

        await Prewarmer.ProcessQueuedAsync(default);

        using var db = NewDb();
        var saved = await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == l1);
        Assert.Equal(Theory, saved.TheoryContent);
        Assert.NotNull(saved.TheoryGeneratedAt);
        VerifyAiCalls(Times.Once());
        _practice.VerifyNoOtherCalls();   // Strict: không reserve credit, không tạo buổi
        Assert.Equal(0, Queue.PendingCount);
    }

    [Fact]
    public async Task Prewarmer_AiLoi_NuotLogVaTiepTucBaiKe()
    {
        // Mutation: bỏ catch trong ProcessQueuedAsync → exception thoát, bài 2 không được sinh → ĐỎ.
        var (_, _, l1, l2, _) = Seed();
        var calls = 0;
        _gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(),
                It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .Returns(() => ++calls == 1
                ? Task.FromException<LessonTheoryResult>(new AiServiceException("AIService /generate-lesson-theory trả 502"))
                : Task.FromResult(new LessonTheoryResult(Theory, [])));
        Assert.True(Queue.TryEnqueue(l1));
        Assert.True(Queue.TryEnqueue(l2));

        await Prewarmer.ProcessQueuedAsync(default);   // không ném

        using var db = NewDb();
        Assert.Null((await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == l1)).TheoryContent);
        Assert.Equal(Theory, (await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == l2)).TheoryContent);
        Assert.Equal(2, calls);
        Assert.Equal(0, Queue.PendingCount);
    }

    [Fact]
    public async Task Prewarmer_DangSinh_UserMoCungBai_ChiGoiAiMotLan()
    {
        // Prewarm đua với GET của người học: cả hai đi qua single-flight → đúng 1 lượt AI.
        var (cand, rid, l1, _, _) = Seed();
        var (gate, titles) = GateAi();
        Assert.True(Queue.TryEnqueue(l1));

        var prewarm = Task.Run(() => Prewarmer.ProcessQueuedAsync(default));
        await WaitUntil(() => { lock (titles) return titles.Count == 1; });   // prewarm đã tới AI
        var user = Task.Run(() => Open(cand, rid, l1));
        await WaitUntil(() => _provider.GetRequiredService<LessonTheorySingleFlight>().IsInFlight(l1));

        gate.SetResult(new LessonTheoryResult(Theory, []));
        await prewarm;
        Assert.Equal(Theory, (await user).TheoryContent);
        VerifyAiCalls(Times.Once());
    }

    [Fact]
    public void Queue_BaiDangInFlight_KhongXepHang()
    {
        // Mutation: bỏ kiểm IsInFlight trong TryEnqueue → ĐỎ.
        var (_, _, l1, _, _) = Seed();
        var (gate, _) = GateAi();
        var flight = _provider.GetRequiredService<LessonTheorySingleFlight>();
        var running = flight.RunAsync(l1);
        Assert.True(flight.IsInFlight(l1));
        Assert.False(Queue.TryEnqueue(l1));
        gate.SetResult(new LessonTheoryResult(Theory, []));
        running.GetAwaiter().GetResult();
    }
}
