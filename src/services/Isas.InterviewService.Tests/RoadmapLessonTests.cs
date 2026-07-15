using System.Reflection;
using System.Security.Claims;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

// BC14 (D20) — lesson: lý thuyết lazy + /start (reserve credit) + Scored→Done / Abandoned→Theory.
public class RoadmapLessonTests
{
    // ── Seed helpers ────────────────────────────────────────────────────────
    // 1 roadmap của candidate, 1 milestone (focusCriteria) status tuỳ, 2 lesson Theory.
    private static Roadmap SeedRoadmap(
        TestDb t, Guid candidateId,
        MilestoneStatus mileStatus = MilestoneStatus.Pending,
        Guid? cvId = null,
        params string[] focus)
    {
        var lessons = new List<RoadmapLesson>
        {
            new() { Id = Guid.NewGuid(), OrderNo = 1, Title = "Lesson 1", Status = LessonStatus.Theory },
            new() { Id = Guid.NewGuid(), OrderNo = 2, Title = "Lesson 2", Status = LessonStatus.Theory }
        };
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "Milestone 1",
            FocusCriteria = focus.Length > 0 ? focus.ToList() : new List<string> { "Clarity", "Depth" },
            Status = mileStatus,
            Lessons = lessons
        };
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            CvId = cvId,
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow,
            Milestones = new List<RoadmapMilestone> { milestone }
        };
        t.Db.Roadmaps.Add(roadmap);
        t.Db.SaveChanges();
        return roadmap;
    }

    private static (Guid roadmapId, Guid milestoneId, Guid lesson1Id, Guid lesson2Id) Ids(Roadmap r)
    {
        var m = r.Milestones.First();
        var ls = m.Lessons.OrderBy(l => l.OrderNo).ToList();
        return (r.Id, m.Id, ls[0].Id, ls[1].Id);
    }

    // Real PracticeService (mock question-gen + reservation + transport) — /start tạo session thật.
    private static PracticeService RealPractice(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, Mock<ICreditReservationClient> reservation)
        => new(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            new Mock<ISessionEventPublisher>().Object, NullLogger<PracticeService>.Instance);

    private static RoadmapsController Controller(
        TestDb t, IPracticeService practice, IAiServiceRoadmapGenerator gen, Guid userId)
    {
        var lessonService = new RoadmapLessonService(
            t.Db, practice, gen, NullLogger<RoadmapLessonService>.Instance);
        var controller = new RoadmapsController(
            new Mock<IRoadmapService>().Object, lessonService,
            new Mock<IRoadmapReportService>().Object, NullLogger<RoadmapsController>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static Mock<IAiServiceQuestionGenerator> QuestionGenOk(
        Action<IReadOnlyList<string>?>? captureFocus = null)
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        var setup = gen.Setup(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()));
        if (captureFocus is not null)
            setup.Callback<string, string?, string?, IReadOnlyList<string>?, CancellationToken>(
                    (_, _, _, focus, _) => captureFocus(focus))
                .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" }, new() { Content = "Q2" } });
        else
            setup.ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" }, new() { Content = "Q2" } });
        return gen;
    }

    private static Mock<ICreditReservationClient> ReserveOk()
    {
        var m = new Mock<ICreditReservationClient>();
        m.Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return m;
    }

    // ── (1) Mở lesson: lần 1 gọi AI + lưu; lần 2 đọc DB (không gọi lại); AI lỗi → 502 ──────
    [Fact]
    public async Task OpenLesson_FirstTimeGeneratesAndPersists_SecondTimeReadsDb()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, user));

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("## Lý thuyết lesson");

        var ctrl = Controller(t, new Mock<IPracticeService>().Object, gen.Object, user);

        // Lần 1: sinh + lưu.
        var ok1 = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmapId, lesson1, default));
        var body1 = Assert.IsType<LessonResponse>(ok1.Value);
        Assert.Equal("## Lý thuyết lesson", body1.TheoryContent);
        Assert.Equal("Theory", body1.Status);

        var saved = await t.NewContext().RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lesson1);
        Assert.Equal("## Lý thuyết lesson", saved.TheoryContent);
        Assert.NotNull(saved.TheoryGeneratedAt);

        // Lần 2: đọc DB, KHÔNG gọi AI thêm.
        var ok2 = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmapId, lesson1, default));
        var body2 = Assert.IsType<LessonResponse>(ok2.Value);
        Assert.Equal("## Lý thuyết lesson", body2.TheoryContent);

        gen.Verify(g => g.GenerateLessonTheoryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OpenLesson_AiFails_Returns502_NoTheorySaved()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, user));

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /generate-lesson-theory trả 500"));

        var ctrl = Controller(t, new Mock<IPracticeService>().Object, gen.Object, user);

        var obj = Assert.IsType<ObjectResult>(await ctrl.OpenLesson(roadmapId, lesson1, default));
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);

        // Chưa lưu → mở lại được.
        var saved = await t.NewContext().RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lesson1);
        Assert.Null(saved.TheoryContent);
    }

    // ── (2) /start có credit → 201 + link lesson + mile InProgress; câu hỏi bám focusCriteria ──
    [Fact]
    public async Task StartLesson_WithCredit_Creates201_LinksLesson_MilestoneInProgress()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user, MilestoneStatus.Pending, cvId: null, "Clarity", "Depth");
        var (roadmapId, milestoneId, lesson1, _) = Ids(r);

        IReadOnlyList<string>? capturedFocus = null;
        var gen = QuestionGenOk(f => capturedFocus = f);
        var reservation = ReserveOk();
        var practice = RealPractice(t, gen, reservation);
        var ctrl = Controller(t, practice, new Mock<IAiServiceRoadmapGenerator>().Object, user);

        var created = Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lesson1, default));
        var session = Assert.IsType<PracticeSessionResponse>(created.Value);
        Assert.Equal("Ready", session.Status);

        // Câu hỏi sinh bám focusCriteria của milestone.
        Assert.NotNull(capturedFocus);
        Assert.Contains("Clarity", capturedFocus!);
        Assert.Contains("Depth", capturedFocus!);

        // reserve đúng ví cá nhân + đúng sessionId trả về.
        reservation.Verify(x => x.ReserveAsync("User", user, session.Id, It.IsAny<CancellationToken>()), Times.Once);

        var db = t.NewContext();
        var lesson = await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lesson1);
        Assert.Equal(LessonStatus.Practicing, lesson.Status);
        Assert.Equal(session.Id, lesson.SessionId);

        var mile = await db.RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == milestoneId);
        Assert.Equal(MilestoneStatus.InProgress, mile.Status);

        Assert.True(await db.PracticeSessions.AsNoTracking().AnyAsync(s => s.Id == session.Id));
    }

    // ── (2b) ví hết → 402, KHÔNG có row session, lesson vẫn Theory, mile vẫn Pending ──────
    [Fact]
    public async Task StartLesson_OutOfCredit_Returns402_NoSession_LessonStaysTheory()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var (roadmapId, milestoneId, lesson1, _) = Ids(r);

        var gen = QuestionGenOk();
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(x => x.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientCreditException("Ví không đủ credit"));
        var practice = RealPractice(t, gen, reservation);
        var ctrl = Controller(t, practice, new Mock<IAiServiceRoadmapGenerator>().Object, user);

        var obj = Assert.IsType<ObjectResult>(await ctrl.StartLesson(roadmapId, lesson1, default));
        Assert.Equal(StatusCodes.Status402PaymentRequired, obj.StatusCode);

        var db = t.NewContext();
        Assert.False(await db.PracticeSessions.AsNoTracking().AnyAsync());
        var lesson = await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lesson1);
        Assert.Equal(LessonStatus.Theory, lesson.Status);
        Assert.Null(lesson.SessionId);
        var mile = await db.RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == milestoneId);
        Assert.Equal(MilestoneStatus.Pending, mile.Status);
        // AI sinh câu hỏi KHÔNG được gọi (reserve chặn trước).
        gen.Verify(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── (3) /start khi đang Practicing → 409, KHÔNG reserve thêm, KHÔNG tạo session mới ──
    [Fact]
    public async Task StartLesson_WhenAlreadyPracticing_Returns409_NoExtraReserve()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var (roadmapId, _, lesson1, _) = Ids(r);

        // Có 1 session đang luyện + lesson đã Practicing (link sẵn).
        var existing = TestDb.Session(user, SessionStatus.Ready);
        t.Db.PracticeSessions.Add(existing);
        await t.Db.SaveChangesAsync();
        await t.Db.RoadmapLessons.Where(l => l.Id == lesson1)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, LessonStatus.Practicing)
                .SetProperty(l => l.SessionId, existing.Id));

        var gen = QuestionGenOk();
        var reservation = ReserveOk();
        var practice = RealPractice(t, gen, reservation);
        var ctrl = Controller(t, practice, new Mock<IAiServiceRoadmapGenerator>().Object, user);

        var conflict = Assert.IsType<ConflictObjectResult>(await ctrl.StartLesson(roadmapId, lesson1, default));
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        // KHÔNG reserve thêm, KHÔNG tạo session mới (vẫn chỉ 1 session cũ).
        reservation.Verify(x => x.ReserveAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, await t.NewContext().PracticeSessions.AsNoTracking().CountAsync());
    }

    // ── (4) session Scored (đi qua SessionScoringNotifier) → lesson Done ─────────────────
    [Fact]
    public async Task SessionScored_ViaNotifier_MarksLinkedLessonDone()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        // session B2C đã Scored + lesson gắn (Practicing).
        var session = TestDb.Session(user, SessionStatus.Scored);
        t.Db.PracticeSessions.Add(session);
        await t.Db.SaveChangesAsync();

        var r = SeedRoadmap(t, user, MilestoneStatus.InProgress);
        var (_, _, lesson1, _) = Ids(r);
        await t.Db.RoadmapLessons.Where(l => l.Id == lesson1)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, LessonStatus.Practicing)
                .SetProperty(l => l.SessionId, session.Id));

        var eventPub = new Mock<ISessionEventPublisher>();
        var notifier = new SessionScoringNotifier(
            t.Db, eventPub.Object, TestDb.ResultService(t.Db), TestDb.Summarizer(),
            TestDb.RoadmapReport(t.Db), NullLogger<SessionScoringNotifier>.Instance);

        await notifier.NotifySessionScoredAsync(session.Id);

        var lesson = await t.NewContext().RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lesson1);
        Assert.Equal(LessonStatus.Done, lesson.Status);
        Assert.Equal(session.Id, lesson.SessionId);   // giữ link để truy vết buổi luyện
    }

    // ── (5) session Abandoned (E3 sweeper) → lesson Theory + session_id null ─────────────
    [Fact]
    public async Task SessionAbandoned_ViaSweeper_RevertsLinkedLessonToTheory()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        // I2: session InProgress quá Deadline (hạn nhận bài), 0 answer → sweeper bỏ ngang.
        var session = TestDb.Session(user, SessionStatus.InProgress,
            deadline: DateTime.UtcNow.AddMinutes(-5));
        t.Db.PracticeSessions.Add(session);
        await t.Db.SaveChangesAsync();

        var r = SeedRoadmap(t, user, MilestoneStatus.InProgress);
        var (_, _, lesson1, _) = Ids(r);
        await t.Db.RoadmapLessons.Where(l => l.Id == lesson1)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, LessonStatus.Practicing)
                .SetProperty(l => l.SessionId, session.Id));

        var (sweeper, _) = BuildSweeper(t);
        await ScanOnce(sweeper);

        var db = t.NewContext();
        var saved = await db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.Equal(SessionStatus.SessionAbandoned, saved.Status);

        var lesson = await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lesson1);
        Assert.Equal(LessonStatus.Theory, lesson.Status);
        Assert.Null(lesson.SessionId);
    }

    // ── (6) owner-only: lesson của người khác → 403; lesson không tồn tại → 404 ──────────
    [Fact]
    public async Task OpenLesson_Stranger_403_Missing_404()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, owner));

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("## Theory");

        // stranger → 403
        var strangerCtrl = Controller(t, new Mock<IPracticeService>().Object, gen.Object, stranger);
        var forbidden = Assert.IsType<ObjectResult>(await strangerCtrl.OpenLesson(roadmapId, lesson1, default));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        // owner + lessonId lạ → 404
        var ownerCtrl = Controller(t, new Mock<IPracticeService>().Object, gen.Object, owner);
        Assert.IsType<NotFoundObjectResult>(await ownerCtrl.OpenLesson(roadmapId, Guid.NewGuid(), default));

        // AI KHÔNG được gọi cho request 403/404.
        gen.Verify(g => g.GenerateLessonTheoryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartLesson_Stranger_403()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, owner));

        var reservation = ReserveOk();
        var practice = RealPractice(t, QuestionGenOk(), reservation);
        var ctrl = Controller(t, practice, new Mock<IAiServiceRoadmapGenerator>().Object, stranger);

        var forbidden = Assert.IsType<ObjectResult>(await ctrl.StartLesson(roadmapId, lesson1, default));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        // Không reserve, không tạo session.
        reservation.Verify(x => x.ReserveAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await t.NewContext().PracticeSessions.AsNoTracking().AnyAsync());
    }

    // ── sweeper harness (mirror SessionAbandonSweeperTests) ─────────────────────────────
    private static async Task ScanOnce(SessionAbandonSweeper s)
    {
        var mi = typeof(SessionAbandonSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(s, new object[] { CancellationToken.None })!;
    }

    private static (SessionAbandonSweeper sweeper, Mock<ISessionEventPublisher> pub) BuildSweeper(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection));
        var provider = services.BuildServiceProvider();

        var pub = new Mock<ISessionEventPublisher>();
        var sweeper = new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            Options.Create(new ScoringOptions()),
            NullLogger<SessionAbandonSweeper>.Instance);
        return (sweeper, pub);
    }
}
