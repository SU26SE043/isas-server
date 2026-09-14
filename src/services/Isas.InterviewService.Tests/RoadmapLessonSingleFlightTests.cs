using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Single-flight sinh lý thuyết bài học (<see cref="LessonTheorySingleFlight"/>) — đo prod 2026-09-14:
/// cùng một bài bị sinh HAI LẦN song song (~50s, ~$0,025 mỗi lượt) vì GET chỉ có guard ghi đè SAU khi
/// đã gọi AI. Bốn tính chất khoá ở đây, mỗi cái một mutation phải ĐỎ:
///   (1) hai request đồng thời → AI gọi ĐÚNG MỘT lần, cả hai nhận cùng bài (bỏ single-flight / Lazy → 2 lượt)
///   (2) người mở đầu tiên huỷ → người chờ vẫn nhận bài, lượt AI KHÔNG bị huỷ (truyền ct request vào thân → ĐỎ)
///   (3) AI lỗi → mọi bên chờ nhận 502, bảng in-flight được gỡ, lần sau sinh lại (bỏ `finally TryRemove` → ĐỎ)
///   (4) bài đã dùng được → GenerateAndPersistAsync KHÔNG gọi AI (bỏ early-return → ĐỎ)
///
/// Harness: hai request chạy trên HAI scope DI (hai DbContext) từ thread khác nhau ⇒ KHÔNG dùng được
/// connection `:memory:` đơn của <see cref="TestDb"/> (SqliteConnection không thread-safe). Dùng DB
/// in-memory ĐẶT TÊN + shared-cache, giữ một connection "keeper" cho DB sống; cổng chặn AI bằng
/// TaskCompletionSource — không Task.Delay, không đua theo thời gian.
/// </summary>
public class RoadmapLessonSingleFlightTests : IDisposable
{
    private readonly SqliteConnection _keeper;
    private readonly string _connString;
    private readonly ServiceProvider _provider;
    private readonly Mock<IAiServiceRoadmapGenerator> _gen = new(MockBehavior.Strict);
    private const string Theory = "# Bài\n\n## Mục 1\nNội dung đủ dùng.";

    public RoadmapLessonSingleFlightTests()
    {
        _connString = $"DataSource=sf-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(_connString);
        _keeper.Open();
        using (var db = NewDb()) db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(_connString).UseSnakeCaseNamingConvention());
        services.AddSingleton(_gen.Object);
        services.AddSingleton(new Mock<IPracticeService>(MockBehavior.Strict).Object);
        services.AddScoped<IRoadmapLessonService, RoadmapLessonService>();
        services.AddSingleton<LessonTheorySingleFlight>();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _keeper.Dispose();
    }

    private InterviewDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseSqlite(_connString).UseSnakeCaseNamingConvention().Options;
        return new InterviewDbContext(opts);
    }

    private (Guid candidateId, Guid roadmapId, Guid lessonId) Seed(string? theory = null)
    {
        using var db = NewDb();
        var candidate = Guid.NewGuid();
        var lesson = new RoadmapLesson { Id = Guid.NewGuid(), OrderNo = 1, Title = "Lesson 1", Status = LessonStatus.Theory, TheoryContent = theory };
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = candidate, JobCategory = JobCategory.BE, Level = RoadmapLevel.Junior,
            Status = RoadmapStatus.Active, CreatedAt = DateTime.UtcNow,
            Milestones = [new RoadmapMilestone
            {
                Id = Guid.NewGuid(), OrderNo = 1, Title = "M1", FocusCriteria = ["Clarity"],
                Status = MilestoneStatus.Pending, Lessons = [lesson]
            }]
        };
        db.Roadmaps.Add(roadmap);
        db.SaveChanges();
        return (candidate, roadmap.Id, lesson.Id);
    }

    /// <summary>Cổng: AI "chạy" cho tới khi test mở cổng; ghi lại ct mà thân single-flight truyền vào.</summary>
    private (TaskCompletionSource<LessonTheoryResult> gate, List<CancellationToken> seenCts) GateAi()
    {
        var gate = new TaskCompletionSource<LessonTheoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<CancellationToken>();
        _gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(),
                It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .Returns((string _, string _, string _, IReadOnlyList<string> _, IReadOnlyList<string>? _,
                      IReadOnlyList<GroundingChunk>? _, IReadOnlyList<CriterionEvidence>? _, RoadmapMode _,
                      CancellationToken ct, IReadOnlyList<RoadmapMistake>? _) =>
            {
                lock (seen) seen.Add(ct);
                return gate.Task;
            });
        return (gate, seen);
    }

    private void VerifyAiCalls(Times times) => _gen.Verify(g => g.GenerateLessonTheoryAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(),
        It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(),
        It.IsAny<IReadOnlyList<RoadmapMistake>?>()), times);

    private Task<LessonResponse> Open(Guid candidate, Guid roadmap, Guid lesson, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            using var scope = _provider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IRoadmapLessonService>();
            return await svc.OpenLessonAsync(candidate, roadmap, lesson, ct);
        });

    private LessonTheorySingleFlight Flight => _provider.GetRequiredService<LessonTheorySingleFlight>();

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (var i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "điều kiện chờ không xảy ra");
    }

    [Fact]
    public async Task OpenLesson_HaiRequestDongThoi_ChiGoiAiMotLan()
    {
        var (cand, rid, lid) = Seed();
        var (gate, seen) = GateAi();

        var a = Open(cand, rid, lid);
        await WaitUntil(() => { lock (seen) return seen.Count == 1; });   // A đã tới AI và đang chờ cổng
        var b = Open(cand, rid, lid);                                         // B nhập vào lượt của A
        await WaitUntil(() => Flight.IsInFlight(lid));

        gate.SetResult(new LessonTheoryResult(Theory, []));
        var (ra, rb) = (await a, await b);

        Assert.Equal(Theory, ra.TheoryContent);
        Assert.Equal(Theory, rb.TheoryContent);
        VerifyAiCalls(Times.Once());
        using var db = NewDb();
        Assert.Equal(Theory, (await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lid)).TheoryContent);
        Assert.False(Flight.IsInFlight(lid));
    }

    [Fact]
    public async Task OpenLesson_NguoiMoDauHuy_NguoiChoVanNhanBai_LuotAiKhongBiHuy()
    {
        var (cand, rid, lid) = Seed();
        var (gate, seen) = GateAi();

        using var ctsA = new CancellationTokenSource();
        var a = Open(cand, rid, lid, ctsA.Token);
        await WaitUntil(() => { lock (seen) return seen.Count == 1; });
        var b = Open(cand, rid, lid);
        await WaitUntil(() => Flight.IsInFlight(lid));

        ctsA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);

        // Thân single-flight nhận token ApplicationStopping (ở đây = None), KHÔNG phải token của A.
        CancellationToken aiCt;
        lock (seen) aiCt = seen[0];
        Assert.False(aiCt.IsCancellationRequested);

        gate.SetResult(new LessonTheoryResult(Theory, []));
        Assert.Equal(Theory, (await b).TheoryContent);
        VerifyAiCalls(Times.Once());
    }

    [Fact]
    public async Task OpenLesson_AiLoi_CaHaiNhanLoi_LanSauSinhLai()
    {
        var (cand, rid, lid) = Seed();
        var (gate, seen) = GateAi();

        var a = Open(cand, rid, lid);
        await WaitUntil(() => { lock (seen) return seen.Count == 1; });
        var b = Open(cand, rid, lid);
        await WaitUntil(() => Flight.IsInFlight(lid));

        gate.SetException(new AiServiceException("AIService /generate-lesson-theory trả 502"));
        await Assert.ThrowsAsync<AiServiceException>(() => a);
        await Assert.ThrowsAsync<AiServiceException>(() => b);
        Assert.False(Flight.IsInFlight(lid));   // lỗi cũng gỡ khỏi bảng — không kẹt Task faulted

        using (var db = NewDb())
            Assert.Null((await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lid)).TheoryContent);

        // Lần sau: AI trả bài → sinh lại được (tổng 2 lượt AI cho 3 request).
        _gen.Reset();
        var (gate2, _) = GateAi();
        gate2.SetResult(new LessonTheoryResult(Theory, []));
        Assert.Equal(Theory, (await Open(cand, rid, lid)).TheoryContent);
        VerifyAiCalls(Times.Once());   // sau Reset chỉ đếm lượt thứ hai
    }

    [Fact]
    public async Task GenerateAndPersist_BaiDaDungDuoc_KhongGoiAi()
    {
        var (_, _, lid) = Seed(theory: Theory);   // Strict mock: gọi AI là ném ngay

        using var scope = _provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IRoadmapLessonService>();
        Assert.False(await svc.GenerateAndPersistAsync(lid));
        VerifyAiCalls(Times.Never());
    }

    [Fact]
    public async Task GenerateAndPersist_LessonKhongTonTai_TraFalseKhongNem()
    {
        using var scope = _provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IRoadmapLessonService>();
        Assert.False(await svc.GenerateAndPersistAsync(Guid.NewGuid()));
        VerifyAiCalls(Times.Never());
    }
}
