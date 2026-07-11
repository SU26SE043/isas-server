using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

// BC12 — POST/GET roadmaps (mock AIService generator + storage). Test qua controller để chốt status code.
public class RoadmapTests
{
    private static FileRecord OwnedFile(Guid fileId, Guid ownerId, string? parsed)
        => new()
        {
            Id = fileId,
            UserId = ownerId,
            FileType = "cv",
            OriginalName = "cv.pdf",
            StoragePath = $"cv/{fileId}.pdf",
            StorageBucket = "isas-files",
            MimeType = "application/pdf",
            FileSize = 1024,
            ParsedText = parsed,
            ParseStatus = "done",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    // 2 milestone, mile1 có 2 lesson, mile2 có 1 lesson.
    private static RoadmapGenAiResult SampleRoadmap()
        => new(new List<GeneratedMilestone>
        {
            new("Milestone 1: Nền tảng", new List<string> { "Clarity", "Depth" },
                new List<GeneratedLesson> { new("Lesson 1.1"), new("Lesson 1.2") }),
            new("Milestone 2: Nâng cao", new List<string> { "Depth" },
                new List<GeneratedLesson> { new("Lesson 2.1") })
        });

    private static Mock<IAiServiceRoadmapGenerator> GenMock(
        RoadmapGenAiResult result, Action<IReadOnlyList<RoadmapWeakness>?>? captureWeaknesses = null)
    {
        var m = new Mock<IAiServiceRoadmapGenerator>();
        var setup = m.Setup(x => x.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapWeakness>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()));
        if (captureWeaknesses is not null)
            setup.Callback<string, string, IReadOnlyList<RoadmapWeakness>?, string?, CancellationToken>(
                    (_, _, w, _, _) => captureWeaknesses(w))
                .ReturnsAsync(result);
        else
            setup.ReturnsAsync(result);
        return m;
    }

    private static RoadmapsController Controller(
        TestDb t, IStorageService storage, IAiServiceRoadmapGenerator gen, Guid userId)
    {
        var service = new RoadmapService(t.Db, storage, gen, NullLogger<RoadmapService>.Instance);
        var controller = new RoadmapsController(
            service, new Mock<IRoadmapLessonService>().Object, NullLogger<RoadmapsController>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    // Seed 1 buổi B2C đã Scored + N tiêu chí có điểm (BC9) → nguồn baseline/weakness.
    private static Guid SeedScoredSession(
        TestDb t, Guid candidateId, params (string name, decimal pct, bool needsImprovement)[] criteria)
    {
        var session = TestDb.Session(candidateId, SessionStatus.Scored, JobCategory.BE);
        t.Db.PracticeSessions.Add(session);
        foreach (var (name, pct, needs) in criteria)
        {
            var crit = TestDb.Criterion(JobCategory.BE, name: name);
            t.Db.RubricCriteria.Add(crit);
            t.Db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                CriterionId = crit.Id,
                CriterionName = name,
                AverageScore = 2m,
                MaxScore = 5,
                Percentage = pct,
                Weight = 1m,
                NeedsImprovement = needs,
                CreatedAt = DateTime.UtcNow
            });
        }
        t.Db.SaveChanges();
        return session.Id;
    }

    // ── (1) POST → 201 + rows đủ 3 bảng, status + order_no đúng ────────────────────
    [Fact]
    public async Task Post_Returns201_AndPersistsThreeTables()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null), default);

        var created = Assert.IsType<CreatedResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var body = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal("Active", body.Status);
        Assert.Equal("BE", body.JobCategory);
        Assert.Equal("Junior", body.Level);
        Assert.Null(body.CvId);

        // milestones theo orderNo 1,2
        Assert.Equal(2, body.Milestones.Count);
        Assert.Equal(new[] { 1, 2 }, body.Milestones.Select(m => m.OrderNo).ToArray());
        var m1 = body.Milestones[0];
        Assert.Equal("Pending", m1.Status);
        Assert.Null(m1.Improvement);
        Assert.Contains("Clarity", m1.FocusCriteria);
        // lessons theo orderNo 1,2, status Theory, theoryContent null
        Assert.Equal(new[] { 1, 2 }, m1.Lessons.Select(l => l.OrderNo).ToArray());
        Assert.All(m1.Lessons, l => Assert.Equal("Theory", l.Status));
        Assert.All(m1.Lessons, l => Assert.Null(l.TheoryContent));
        Assert.All(m1.Lessons, l => Assert.Null(l.SessionId));

        // DB round-trip (AsNoTracking → đọc lại từ SQLite qua value converter)
        var roadmapRow = await t.Db.Roadmaps.AsNoTracking().SingleAsync();
        Assert.Equal(user, roadmapRow.CandidateId);
        Assert.Equal(RoadmapStatus.Active, roadmapRow.Status);
        Assert.Equal(RoadmapLevel.Junior, roadmapRow.Level);
        Assert.Null(roadmapRow.Baseline);
        Assert.Null(roadmapRow.SourceSessionIds);

        Assert.Equal(2, await t.Db.RoadmapMilestones.CountAsync());
        Assert.Equal(3, await t.Db.RoadmapLessons.CountAsync());
        Assert.All(await t.Db.RoadmapMilestones.AsNoTracking().ToListAsync(),
            m => Assert.Equal(MilestoneStatus.Pending, m.Status));
        Assert.All(await t.Db.RoadmapLessons.AsNoTracking().ToListAsync(),
            l => Assert.Equal(LessonStatus.Theory, l.Status));
    }

    // ── (2a) baseline: có ≥1 session Scored → baseline có %, weakness gửi xuống AI ──
    [Fact]
    public async Task Post_WithScoredSessions_SnapshotsBaselineAndWeaknesses()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sessionId = SeedScoredSession(t, user,
            ("Clarity", 40m, true),    // yếu
            ("Depth", 80m, false));    // mạnh

        IReadOnlyList<RoadmapWeakness>? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), w => captured = w).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null), default);
        Assert.IsType<CreatedResult>(result);

        var row = await t.Db.Roadmaps.AsNoTracking().SingleAsync();
        Assert.NotNull(row.Baseline);
        Assert.Equal(40m, row.Baseline!["Clarity"]);
        Assert.Equal(80m, row.Baseline["Depth"]);
        Assert.NotNull(row.SourceSessionIds);
        Assert.Contains(sessionId, row.SourceSessionIds!);

        // Chỉ tiêu chí needsImprovement được gửi xuống AI làm weakness.
        Assert.NotNull(captured);
        var weak = Assert.Single(captured!);
        Assert.Equal("Clarity", weak.CriterionName);
        Assert.Equal(40m, weak.Percentage);
    }

    // ── (2b) không có buổi nào đã chấm → baseline null, weakness null, vẫn 201 ──────
    [Fact]
    public async Task Post_NoScoredSessions_BaselineNull()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        IReadOnlyList<RoadmapWeakness>? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), w => captured = w).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.FE, RoadmapLevel.Fresher, null), default);
        Assert.IsType<CreatedResult>(result);

        var row = await t.Db.Roadmaps.AsNoTracking().SingleAsync();
        Assert.Null(row.Baseline);
        Assert.Null(row.SourceSessionIds);
        Assert.Null(captured);   // rỗng → AI sinh roadmap chuẩn theo level
    }

    // ── (3) GET owner → đầy đủ; stranger → 403; id lạ → 404 ────────────────────────
    [Fact]
    public async Task Get_Owner_Full_Stranger_403_Missing_404()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        var ownerCtrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, owner);
        var created = Assert.IsType<CreatedResult>(
            await ownerCtrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null), default));
        var id = ((RoadmapResponse)created.Value!).Id;

        // owner → 200 đầy đủ
        var ok = Assert.IsType<OkObjectResult>(await ownerCtrl.Get(id, default));
        var body = Assert.IsType<RoadmapResponse>(ok.Value);
        Assert.Equal(id, body.Id);
        Assert.Equal(2, body.Milestones.Count);

        // stranger → 403
        var strangerCtrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, stranger);
        var forbidden = Assert.IsType<ObjectResult>(await strangerCtrl.Get(id, default));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        // id không tồn tại → 404
        Assert.IsType<NotFoundObjectResult>(await ownerCtrl.Get(Guid.NewGuid(), default));
    }

    // ── (3b) list chỉ của mình + KHÔNG kèm theoryContent (GET {id} thì có) ──────────
    [Fact]
    public async Task List_OwnOnly_OmitsTheoryContent()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);
        var created = Assert.IsType<CreatedResult>(
            await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null), default));
        var id = ((RoadmapResponse)created.Value!).Id;

        // Giả lập BC14 đã sinh lý thuyết cho 1 lesson.
        var lesson = await t.Db.RoadmapLessons.OrderBy(l => l.OrderNo).FirstAsync();
        lesson.TheoryContent = "## Lý thuyết";
        lesson.TheoryGeneratedAt = DateTime.UtcNow;
        await t.Db.SaveChangesAsync();

        // GET {id} → có theoryContent
        var getOk = Assert.IsType<OkObjectResult>(await ctrl.Get(id, default));
        var detail = Assert.IsType<RoadmapResponse>(getOk.Value);
        Assert.Contains(detail.Milestones.SelectMany(m => m.Lessons), l => l.TheoryContent == "## Lý thuyết");

        // LIST → cùng lesson đó theoryContent null
        var listOk = Assert.IsType<OkObjectResult>(await ctrl.List(default));
        var items = Assert.IsAssignableFrom<IReadOnlyList<RoadmapResponse>>(listOk.Value);
        var listed = Assert.Single(items);
        Assert.All(listed.Milestones.SelectMany(m => m.Lessons), l => Assert.Null(l.TheoryContent));

        // user khác → list rỗng
        var otherCtrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, other);
        var otherOk = Assert.IsType<OkObjectResult>(await otherCtrl.List(default));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<RoadmapResponse>>(otherOk.Value));
    }

    // ── (4) cvId không phải của mình → 403; không tồn tại → 404 ─────────────────────
    [Fact]
    public async Task Post_CvOwnedByOther_Returns403_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, Guid.NewGuid(), "CV nội dung"));   // chủ khác

        var ctrl = Controller(t, storage.Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, cvId), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
    }

    [Fact]
    public async Task Post_CvNotFound_Returns404_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileRecord?)null);

        var ctrl = Controller(t, storage.Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, Guid.NewGuid()), default);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
    }

    // ── (5) generator throw → 502, KHÔNG có row roadmap (rollback) ──────────────────
    [Fact]
    public async Task Post_GeneratorFails_Returns502_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(x => x.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapWeakness>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /generate-roadmap trả 500"));

        var ctrl = Controller(t, new Mock<IStorageService>().Object, gen.Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
        Assert.False(await t.Db.RoadmapMilestones.AnyAsync());
        Assert.False(await t.Db.RoadmapLessons.AnyAsync());
    }
}
