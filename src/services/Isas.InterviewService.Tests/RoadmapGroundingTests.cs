using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

// RAG grounding — Cách 2 (precompute lúc tạo roadmap) + narrow-khi-mở-lesson.
public class RoadmapGroundingTests
{
    private static GroundingChunk Chunk(string id) => new(id, "content " + id, $"https://react.dev/{id}", "title " + id);

    private static RoadmapGenAiResult SampleRoadmap() => new(new List<GeneratedMilestone>
    {
        new("M1", new List<string> { "Clarity" }, new List<GeneratedLesson> { new("L1"), new("L2") }),
        new("M2", new List<string> { "Depth" }, new List<GeneratedLesson> { new("L3") })
    });

    // ── PRECOMPUTE lúc CreateAsync ──────────────────────────────────────────────
    [Fact]
    public async Task Create_GroundingEnabled_PrecomputesGroundingRefsPerLesson()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                 It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleRoadmap());

        // 1 batch → 3 lesson: L1 có nguồn, L2 miss, L3 có nguồn.
        var knowledge = new Mock<IKnowledgeService>();
        knowledge.Setup(k => k.RetrieveBatchAsync("FE", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IReadOnlyList<GroundingChunk>>
            {
                new List<GroundingChunk> { Chunk("A") },
                Array.Empty<GroundingChunk>(),
                new List<GroundingChunk> { Chunk("B") }
            });

        var svc = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, NullLogger<RoadmapService>.Instance,
            knowledge: knowledge.Object, groundingOptions: Options.Create(new GroundingOptions { Enabled = true }));

        var res = await svc.CreateAsync(candidate, new CreateRoadmapRequest(JobCategory.FE, RoadmapLevel.Fresher, null), default);

        var lessons = await t.NewContext().RoadmapLessons.AsNoTracking().OrderBy(l => l.Title).ToListAsync();
        Assert.Equal(3, lessons.Count);
        // grounding ĐÃ chạy → LUÔN list (không null), rỗng cho lesson miss.
        Assert.All(lessons, l => Assert.NotNull(l.GroundingRefs));
        Assert.Single(lessons.First(l => l.Title == "L1").GroundingRefs!);
        Assert.Empty(lessons.First(l => l.Title == "L2").GroundingRefs!);
        Assert.Single(lessons.First(l => l.Title == "L3").GroundingRefs!);

        knowledge.Verify(k => k.RetrieveBatchAsync("FE", It.Is<IReadOnlyList<string>>(q => q.Count == 3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_GroundingDisabled_GroundingRefsNull_NoPrecompute()
    {
        using var t = new TestDb();
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                 It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleRoadmap());
        var knowledge = new Mock<IKnowledgeService>();

        var svc = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, NullLogger<RoadmapService>.Instance,
            knowledge: knowledge.Object, groundingOptions: Options.Create(new GroundingOptions { Enabled = false }));

        await svc.CreateAsync(Guid.NewGuid(), new CreateRoadmapRequest(JobCategory.FE, RoadmapLevel.Fresher, null), default);

        var lessons = await t.NewContext().RoadmapLessons.AsNoTracking().ToListAsync();
        Assert.All(lessons, l => Assert.Null(l.GroundingRefs));
        knowledge.Verify(k => k.RetrieveBatchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── OPEN lesson — feed snapshot precompute + narrow về chunk được cite ───────
    [Fact]
    public async Task OpenLesson_FeedsPrecomputedGrounding_NarrowsToCited()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var lessonId = SeedRoadmapWithGrounding(t, candidate, new List<GroundingChunk> { Chunk("A"), Chunk("B") }, out var roadmapId);

        IReadOnlyList<GroundingChunk>? passedGrounding = null;
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<RoadmapMode>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .Callback<string, string, string, IReadOnlyList<string>, IReadOnlyList<string>?, IReadOnlyList<GroundingChunk>?, IReadOnlyList<CriterionEvidence>?, RoadmapMode, CancellationToken, IReadOnlyList<RoadmapMistake>?>(
                (_, _, _, _, _, grounding, _, _, _, _) => passedGrounding = grounding)
            // AI cite CHỈ "A" (không cite "B").
            .ReturnsAsync(new LessonTheoryResult("## Lý thuyết", [], new List<string> { "A" }));

        var svc = new RoadmapLessonService(t.Db, new Mock<IPracticeService>().Object, gen.Object, NullLogger<RoadmapLessonService>.Instance);
        var res = await svc.OpenLessonAsync(candidate, roadmapId, lessonId, default);

        // Snapshot precompute (A+B) được FEED vào generator.
        Assert.NotNull(passedGrounding);
        Assert.Equal(2, passedGrounding!.Count);

        // Citation NARROW về đúng chunk được cite ("A"); "B" bị bỏ (không cite).
        Assert.NotNull(res.Citations);
        Assert.Single(res.Citations!);
        Assert.Equal("A", res.Citations![0].ChunkId);

        // Persist: grounding_refs narrow còn 1 (A).
        var saved = await t.NewContext().RoadmapLessons.AsNoTracking().SingleAsync(l => l.Id == lessonId);
        Assert.Single(saved.GroundingRefs!);
        Assert.Equal("A", saved.GroundingRefs![0].ChunkId);
    }

    [Fact]
    public async Task OpenLesson_NoPrecompute_GroundingNull_CitationsNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        // GroundingRefs null (roadmap cũ chưa precompute).
        var lessonId = SeedRoadmapWithGrounding(t, candidate, groundingRefs: null, out var roadmapId);

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<RoadmapMode>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LessonTheoryResult("## Lý thuyết", [], null));

        var svc = new RoadmapLessonService(t.Db, new Mock<IPracticeService>().Object, gen.Object, NullLogger<RoadmapLessonService>.Instance);
        var res = await svc.OpenLessonAsync(candidate, roadmapId, lessonId, default);

        Assert.Null(res.Citations);   // chưa precompute → null (không claim nguồn)
        var saved = await t.NewContext().RoadmapLessons.AsNoTracking().SingleAsync(l => l.Id == lessonId);
        Assert.Null(saved.GroundingRefs);
    }

    private static Guid SeedRoadmapWithGrounding(
        TestDb t, Guid candidate, List<GroundingChunk>? groundingRefs, out Guid roadmapId)
    {
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate,
            JobCategory = JobCategory.FE,
            Level = RoadmapLevel.Fresher,
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "M1",
            FocusCriteria = new List<string> { "Clarity" },
            Status = MilestoneStatus.Pending
        };
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "L1",
            Status = LessonStatus.Theory,
            TheoryContent = null,
            GroundingRefs = groundingRefs
        };
        milestone.Lessons.Add(lesson);
        roadmap.Milestones.Add(milestone);
        t.Db.Set<Roadmap>().Add(roadmap);
        t.Db.SaveChanges();

        roadmapId = roadmap.Id;
        return lesson.Id;
    }
}
