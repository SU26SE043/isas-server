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
using Microsoft.Extensions.Configuration;
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
            NullLogger<PracticeService>.Instance);

    // BE-2 — cùng RealPractice nhưng bật `Interview:Bilingual:Enabled`, cần cho buổi luyện của
    // roadmap tiếng Anh: thiếu cờ này, `ValidateLanguage("en")` ném "Bilingual interview chưa được
    // bật" dù roadmap đã hợp lệ ở tầng của nó (mẫu `BilingualPractice` trong EvidenceDrivenPr160Tests).
    private static PracticeService RealPracticeBilingual(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, Mock<ICreditReservationClient> reservation)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Interview:Bilingual:Enabled"] = "true"
        }).Build();
        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance,
            config: config);
    }

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
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));
        if (captureFocus is not null)
            setup.Callback<string, string?, string?, IReadOnlyList<string>?, int?, string, CancellationToken>(
                    (_, _, _, focus, _, _, _) => captureFocus(focus))
                .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" }, new() { Content = "Q2" } });
        else
            setup.ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" }, new() { Content = "Q2" } });
        return gen;
    }

    // BE-2 — mock overload NGÔN-NGỮ-HOÁ (jobCategory,cvText,jdText,focusCriteria,count,grounding,
    // language,seniority,ct): đường được gọi khi `session.Language != "vi"` và không có tiêu chí
    // NỘI DUNG nào để gắn nhãn (targetable.Count == 0, ca của rubric EN mới seed 1 tiêu chí Always
    // trong test này) — KHÁC overload 7-tham-số mà `QuestionGenOk` mock cho đường "vi".
    private static Mock<IAiServiceQuestionGenerator> QuestionGenOkEnglish()
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                new List<GeneratedQuestion> { new() { Content = "Q1" }, new() { Content = "Q2" } },
                Array.Empty<QuestionCitationDto>()));
        return gen;
    }

    private static Mock<ICreditReservationClient> ReserveOk()
    {
        var m = new Mock<ICreditReservationClient>();
        m.Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return m;
    }

    // Bài giảng "dùng được" phải có thân bài, không chỉ mỗi dòng tiêu đề — bài một dòng nay được
    // coi là CHƯA sinh và sẽ sinh lại (xem RoadmapLessonService.HasUsableTheory). Seed cũ ở đây là
    // đúng một dòng nên phải đổi, nếu không test này đỏ vì tiền đề chứ không vì hành vi.
    private const string TheoryDuDung = "## Lý thuyết lesson\n\nNội dung giải thích chi tiết.";

    // ── (0) Bài hỏng CŨ tự sinh lại ────────────────────────────────────────────────────────
    // Sự cố 2026-08-03 trên deploy: bài "Giới thiệu về Business Analyst và vai trò cốt lõi" lưu đúng
    // MỘT DÒNG tiêu đề, không thân bài. Vì lý thuyết chỉ sinh một lần rồi lưu, người học mở lại vẫn
    // thấy trang trắng — vĩnh viễn, không có đường nào tự cứu.
    [Fact]
    public async Task OpenLesson_BaiCuChiCoMotDong_SinhLaiVaGhiDe()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, user));

        // Bài hỏng đã nằm sẵn trong DB (sinh trước bản vá này).
        await t.Db.RoadmapLessons.Where(l => l.Id == lesson1)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.TheoryContent, "# Giới thiệu về Business Analyst")
                .SetProperty(l => l.TheoryGeneratedAt, DateTime.UtcNow.AddDays(-1)));

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LessonTheoryResult(TheoryDuDung, []));

        var ctrl = Controller(t, new Mock<IPracticeService>().Object, gen.Object, user);
        var ok = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmapId, lesson1, default));

        // Trả bản MỚI cho người học...
        Assert.Equal(TheoryDuDung, Assert.IsType<LessonResponse>(ok.Value).TheoryContent);
        // ...và ghi đè thật xuống DB. Vế này bắt lỗi "chỉ sửa nhánh đọc mà quên vị ngữ .Where lúc
        // ghi": khi đó AI vẫn bị gọi mỗi lần mở nhưng không bao giờ ghi được — đốt token im lặng.
        var saved = await t.NewContext().RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lesson1);
        Assert.Equal(TheoryDuDung, saved.TheoryContent);
    }

    // Bài KHÔNG có xuống dòng nhưng CÓ mục con là bài có nội dung, chỉ trình bày liền dòng —
    // preflight DB thật 2026-08-04 có đúng một bài như vậy, dài 7.904 ký tự. Sinh đè nó là đem một
    // bài có nội dung đổi lấy một canh bạc.
    [Fact]
    public async Task OpenLesson_BaiMotDongNhungCoMucCon_KhongSinhLai()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, user));

        const string lienDong = "# Ôn tập cấu trúc dữ liệu ## Mảng và danh sách Nội dung... ## Cây Nội dung...";
        await t.Db.RoadmapLessons.Where(l => l.Id == lesson1)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.TheoryContent, lienDong));

        var gen = new Mock<IAiServiceRoadmapGenerator>(MockBehavior.Strict);   // gọi AI = đỏ ngay
        var ctrl = Controller(t, new Mock<IPracticeService>().Object, gen.Object, user);

        var ok = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmapId, lesson1, default));
        Assert.Equal(lienDong, Assert.IsType<LessonResponse>(ok.Value).TheoryContent);
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
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LessonTheoryResult(TheoryDuDung, []));

        var ctrl = Controller(t, new Mock<IPracticeService>().Object, gen.Object, user);

        // Lần 1: sinh + lưu.
        var ok1 = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmapId, lesson1, default));
        var body1 = Assert.IsType<LessonResponse>(ok1.Value);
        Assert.Equal(TheoryDuDung, body1.TheoryContent);
        Assert.Equal("Theory", body1.Status);

        var saved = await t.NewContext().RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lesson1);
        Assert.Equal(TheoryDuDung, saved.TheoryContent);
        Assert.NotNull(saved.TheoryGeneratedAt);

        // Lần 2: đọc DB, KHÔNG gọi AI thêm.
        var ok2 = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmapId, lesson1, default));
        var body2 = Assert.IsType<LessonResponse>(ok2.Value);
        Assert.Equal(TheoryDuDung, body2.TheoryContent);

        gen.Verify(g => g.GenerateLessonTheoryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>()),
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
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>()))
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

    // ── (2c) BE-2 — buổi luyện của lesson phải LẤY ĐÚNG ngôn ngữ của roadmap ────────────────
    //
    // Trước bản vá: `CreatePracticeSessionRequest` dựng ở `RoadmapLessonService.StartLessonAsync`
    // KHÔNG truyền `Language` ⇒ rơi về default `null` ⇒ `ValidateLanguage` luôn trả "vi", bất kể
    // roadmap là tiếng gì. Cùng lớp lỗi với Seniority (đã sửa ngay trên) — chưa đổ máu chỉ vì tình
    // cờ: 8 buổi hiện có trên production đều bắt nguồn từ roadmap tiếng Việt, nhưng đã có 1 roadmap
    // tiếng Anh chưa ai bấm Bắt đầu.
    [Fact]
    public async Task StartLesson_VietnameseRoadmap_CreatesVietnameseSession()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user, MilestoneStatus.Pending, cvId: null, "Clarity", "Depth");
        var (roadmapId, _, lesson1, _) = Ids(r);
        Assert.Equal("vi", r.Language);   // mặc định entity — khoá tiền đề của test này

        var gen = QuestionGenOk();
        var reservation = ReserveOk();
        var practice = RealPractice(t, gen, reservation);
        var ctrl = Controller(t, practice, new Mock<IAiServiceRoadmapGenerator>().Object, user);

        var created = Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lesson1, default));
        var session = Assert.IsType<PracticeSessionResponse>(created.Value);
        Assert.Equal("vi", session.Language);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.Equal("vi", saved.Language);
    }

    [Fact]
    public async Task StartLesson_EnglishRoadmap_CreatesEnglishSession()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user, MilestoneStatus.Pending, cvId: null, "Clarity", "Depth");
        var (roadmapId, _, lesson1, _) = Ids(r);

        // Roadmap tiếng Anh — mô phỏng đúng ca đang treo trên production (chưa ai bấm Bắt đầu).
        await t.Db.Roadmaps.Where(x => x.Id == roadmapId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Language, "en"));
        // `EnsureRubricExistsAsync` đòi rubric EN đang active cho (BE, en) trước khi cho tạo buổi —
        // mẫu seed của RubricLanguageQ9Tests.
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, language: "en"));
        await t.Db.SaveChangesAsync();

        var gen = QuestionGenOkEnglish();
        var reservation = ReserveOk();
        var practice = RealPracticeBilingual(t, gen, reservation);
        var ctrl = Controller(t, practice, new Mock<IAiServiceRoadmapGenerator>().Object, user);

        var created = Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lesson1, default));
        var session = Assert.IsType<PracticeSessionResponse>(created.Value);
        Assert.Equal("en", session.Language);

        var saved = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.Equal("en", saved.Language);
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
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var notifier = TestDb.Notifier(t.Db);

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

        var sweeper = BuildSweeper(t);
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
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LessonTheoryResult("## Theory", []));

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
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>()),
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

    // ── Số câu buổi luyện trong bài học TĨNH theo RoadmapOptions ────────────────────────
    //
    // Trước bản vá: `RoadmapLessonService.BeginSessionAsync` dựng `CreatePracticeSessionRequest`
    // KHÔNG truyền `QuestionCount`/`AdaptiveEnabled` ⇒ rơi về mặc định toàn cục `Adaptive:*`
    // (Enabled=true, SeedCount=5, MaxDeepPerQuestion=3) ⇒ buổi ra 5 câu gốc + chuỗi đào sâu + câu
    // bù tự động tới trần 20 — không phải 5 câu cố định như người học tưởng.
    private static (Mock<IPracticeService> practice, Func<CreatePracticeSessionRequest?> captured)
        CapturingPractice(TestDb t)
    {
        CreatePracticeSessionRequest? captured = null;
        var practice = new Mock<IPracticeService>();
        practice.Setup(p => p.CreateLessonSessionAsync(
                It.IsAny<Guid>(), It.IsAny<CreatePracticeSessionRequest>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid cid, CreatePracticeSessionRequest req, Guid sid,
                       IReadOnlyList<string>? _, CancellationToken _) =>
            {
                captured = req;
                // Link lesson sau đó chạy FK roadmap_lessons.session_id (SQLite CÓ enforce FK
                // trong EF10) — mock trả DTO suông sẽ nổ FK, không phải lỗi code (mẫu Seniority
                // test ở EvidenceDrivenPr160Tests.cs).
                var s = TestDb.Session(cid, SessionStatus.Ready);
                s.Id = sid;
                t.Db.PracticeSessions.Add(s);
                t.Db.SaveChanges();
            })
            .ReturnsAsync(new PracticeSessionResponse(
                Guid.NewGuid(), "Ready", "BE", "vi", null, null, DateTime.UtcNow, null, []));
        return (practice, () => captured);
    }

    private static RoadmapsController ControllerWithRoadmapOptions(
        TestDb t, IPracticeService practice, Guid userId, RoadmapOptions? roadmapOptions = null)
    {
        var lessonService = new RoadmapLessonService(
            t.Db, practice, new Mock<IAiServiceRoadmapGenerator>().Object,
            NullLogger<RoadmapLessonService>.Instance,
            scoringOptions: null,
            roadmapOptions: Options.Create(roadmapOptions ?? new RoadmapOptions()));
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

    // (a) Mặc định: đúng 5 câu, adaptive tắt — không câu chèn, không câu bù.
    [Fact]
    public async Task StartLesson_MacDinh_5CauCoDinh_AdaptiveTat()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, user));

        var (practice, captured) = CapturingPractice(t);
        var ctrl = ControllerWithRoadmapOptions(t, practice.Object, user);

        Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lesson1, default));

        Assert.NotNull(captured());
        Assert.Equal(5, captured()!.QuestionCount);
        Assert.False(captured()!.AdaptiveEnabled);
    }

    // (b) Cấu hình có tác dụng THẬT — không phải hằng số ghi cứng.
    [Fact]
    public async Task StartLesson_CauHinhDoi_SoCauTheoCauHinh()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, user));

        var (practice, captured) = CapturingPractice(t);
        var ctrl = ControllerWithRoadmapOptions(
            t, practice.Object, user, new RoadmapOptions { LessonQuestionCount = 8 });

        Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lesson1, default));

        Assert.Equal(8, captured()!.QuestionCount);
    }

    // (c) Cấu hình sai — kẹp về dải hợp lệ, KHÔNG ném.
    [Theory]
    [InlineData(99, 20)]
    [InlineData(0, 1)]
    public async Task StartLesson_CauHinhNgoaiDai_KepKhongNem(int configured, int expected)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, user));

        var (practice, captured) = CapturingPractice(t);
        var ctrl = ControllerWithRoadmapOptions(
            t, practice.Object, user, new RoadmapOptions { LessonQuestionCount = configured });

        Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lesson1, default));

        Assert.Equal(expected, captured()!.QuestionCount);
    }

    // (d) Đường lùi vẫn sống: bật lại adaptive cho bài học qua cấu hình.
    [Fact]
    public async Task StartLesson_LessonAdaptiveEnabledTrue_BatAdaptive()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lesson1, _) = Ids(SeedRoadmap(t, user));

        var (practice, captured) = CapturingPractice(t);
        var ctrl = ControllerWithRoadmapOptions(
            t, practice.Object, user, new RoadmapOptions { LessonAdaptiveEnabled = true });

        Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lesson1, default));

        Assert.True(captured()!.AdaptiveEnabled);
    }

    // (e) RetryLessonAsync đi qua CÙNG thân BeginSessionAsync — khoá luôn đường làm lại, kẻo sau
    // này ai tách hai đường ra thì chỉ một bên được vá.
    [Fact]
    public async Task RetryLesson_5CauCoDinh_AdaptiveTat()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var (roadmapId, _, lesson1, _) = Ids(r);
        await t.Db.RoadmapLessons.Where(l => l.Id == lesson1)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LessonStatus.Done));

        var (practice, captured) = CapturingPractice(t);
        var ctrl = ControllerWithRoadmapOptions(t, practice.Object, user);

        Assert.IsType<OkObjectResult>(await ctrl.RetryLesson(roadmapId, lesson1, default));

        Assert.Equal(5, captured()!.QuestionCount);
        Assert.False(captured()!.AdaptiveEnabled);
    }

    // ── sweeper harness (mirror SessionAbandonSweeperTests) ─────────────────────────────
    private static async Task ScanOnce(SessionAbandonSweeper s)
    {
        var mi = typeof(SessionAbandonSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(s, new object[] { CancellationToken.None })!;
    }

    private static SessionAbandonSweeper BuildSweeper(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        return new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ScoringOptions()),
            NullLogger<SessionAbandonSweeper>.Instance);
    }
}
