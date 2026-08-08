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

// RAG grounding — guard citation (drop-by-construction) + wire PracticeService 3 trạng thái (null/[]/non-empty).
public class GroundingWireTests
{
    private static GroundingChunk Chunk(string id) => new(id, "content " + id, $"https://react.dev/{id}", "title " + id);

    // ── GroundingMapper — GUARD chống bịa nguồn ─────────────────────────────────
    [Fact]
    public void ResolveCitations_DropsIdNotInProvidedSet()
    {
        var provided = new List<GroundingChunk> { Chunk("A"), Chunk("B") };
        // AI cite "A" (hợp lệ) + "GHOST" (bịa, không nằm trong tập cấp).
        var result = GroundingMapper.ResolveCitations(provided, new[] { "A", "GHOST" });

        Assert.Single(result);
        Assert.Equal("A", result[0].ChunkId);
        Assert.DoesNotContain(result, c => c.ChunkId == "GHOST");   // guard: id lạ bị DROP
    }

    [Fact]
    public void ResolveCitations_EmptyProvided_ReturnsEmpty()
        => Assert.Empty(GroundingMapper.ResolveCitations(new List<GroundingChunk>(), new[] { "A" }));

    [Fact]
    public void ResolveCitations_EmptyCited_ReturnsEmpty()
        => Assert.Empty(GroundingMapper.ResolveCitations(new List<GroundingChunk> { Chunk("A") }, Array.Empty<string>()));

    [Fact]
    public void ResolveCitations_DedupsRepeatedCite()
    {
        var provided = new List<GroundingChunk> { Chunk("A") };
        var result = GroundingMapper.ResolveCitations(provided, new[] { "A", "A" });
        Assert.Single(result);
    }

    [Fact]
    public void ToCitations_ThreeStates()
    {
        Assert.Null(GroundingMapper.ToCitations(null));                                   // không grounding
        Assert.Empty(GroundingMapper.ToCitations(new List<GroundingChunk>())!);           // ungrounded ([])
        var mapped = GroundingMapper.ToCitations(new List<GroundingChunk> { Chunk("A") })!;
        Assert.Single(mapped);
        Assert.Equal("A", mapped[0].ChunkId);
        Assert.Equal("title A", mapped[0].SourceTitle);
    }

    // ── PracticeService wire — 3 trạng thái GroundingRefs ───────────────────────
    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen,
        IKnowledgeService? knowledge, bool groundingEnabled)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, notifier.Object,
            reservation.Object, NullLogger<PracticeService>.Instance,
            knowledge: knowledge,
            groundingOptions: Options.Create(new GroundingOptions { Enabled = groundingEnabled }));
    }

    [Fact]
    public async Task Create_GroundingEnabled_ResolvesCitationsPerQuestion()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        // Retrieve trả 1 chunk uy tín "A".
        var knowledge = new Mock<IKnowledgeService>();
        knowledge.Setup(k => k.RetrieveAsync("FE", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GroundingChunk> { Chunk("A") });

        // AI: Q1 cite "A", Q2 KHÔNG cite gì.
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                // SEN1 — đường grounded nay gọi overload `language` (overload `grounding+ct` không
                // mang được `seniority`, xem IAiServiceQuestionGenerator): +1 tham số `seniority`.
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                new List<GeneratedQuestion> { new() { Content = "Q1" }, new() { Content = "Q2" } },
                new List<QuestionCitationDto> { new(0, new[] { "A" }) }));

        var svc = Build(t, gen, knowledge.Object, groundingEnabled: true);
        var res = await svc.CreateSessionAsync(candidate, new CreatePracticeSessionRequest(null, null, JobCategory.FE), default);

        // Q1 (index 0) grounded, Q2 (index 1) đã grounding nhưng không cite → [] (KHÔNG null).
        var q1 = res.Questions.Single(q => q.OrderNo == 1);
        var q2 = res.Questions.Single(q => q.OrderNo == 2);
        Assert.NotNull(q1.Citations);
        Assert.Single(q1.Citations!);
        Assert.Equal("https://react.dev/A", q1.Citations![0].SourceUrl);
        Assert.NotNull(q2.Citations);
        Assert.Empty(q2.Citations!);   // đã grounding, ungrounded → [] (FE nhãn nổi bật)

        // Persist đúng: đọc lại từ DB.
        var saved = await t.NewContext().PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == res.Id).OrderBy(q => q.OrderNo).ToListAsync();
        Assert.Single(saved[0].GroundingRefs!);
        Assert.Empty(saved[1].GroundingRefs!);
    }

    [Fact]
    public async Task Create_GroundingEnabled_RetrievalMiss_DropsGhostCite_Empty()
    {
        using var t = new TestDb();
        // Retrieval MISS (rỗng). AI vẫn "cite" "A" (bịa) → guard drop (A không nằm trong tập cấp = rỗng) → [].
        var knowledge = new Mock<IKnowledgeService>();
        knowledge.Setup(k => k.RetrieveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GroundingChunk>());

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                // SEN1 — đường grounded nay gọi overload `language` (overload `grounding+ct` không
                // mang được `seniority`, xem IAiServiceQuestionGenerator): +1 tham số `seniority`.
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                new List<GeneratedQuestion> { new() { Content = "Q1" } },
                new List<QuestionCitationDto> { new(0, new[] { "A" }) }));

        var svc = Build(t, gen, knowledge.Object, groundingEnabled: true);
        var res = await svc.CreateSessionAsync(Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.FE), default);

        var q1 = res.Questions.Single();
        Assert.NotNull(q1.Citations);   // đã grounding → emit []
        Assert.Empty(q1.Citations!);    // ghost "A" bị guard drop
    }

    [Fact]
    public async Task Create_GroundingDisabled_CitationsNull_KnowledgeNotCalled()
    {
        using var t = new TestDb();
        var knowledge = new Mock<IKnowledgeService>();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        // Grounding tắt → PracticeService gọi overload CŨ 4 tham số (không grounding).
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });

        var svc = Build(t, gen, knowledge.Object, groundingEnabled: false);
        var res = await svc.CreateSessionAsync(Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.FE), default);

        Assert.Null(res.Questions.Single().Citations);   // không đi đường grounding → null (FE không nhãn)
        knowledge.Verify(k => k.RetrieveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var saved = await t.NewContext().PracticeQuestions.AsNoTracking().SingleAsync(q => q.SessionId == res.Id);
        Assert.Null(saved.GroundingRefs);
    }
}
