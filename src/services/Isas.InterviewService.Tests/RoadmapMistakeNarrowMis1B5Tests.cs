using System.Net;
using System.Text;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// MIS1-B5 — <c>RoadmapService.CreateAsync</c> PHẢI narrow lại <c>mistakeIds</c> AI trả về theo
/// đúng tập id THẬT đã trích (<see cref="RoadmapMistakeLoader"/>) trước khi ghi <c>MistakeRefs</c>
/// xuống DB — chống bịa BY-CONSTRUCTION (mẫu <c>NarrowToCited</c> của RoadmapLessonService/RAG).
///
/// <para>JSON response dựng THẬT (không qua mock interface) để buộc
/// <c>System.Text.Json</c> deserialize <c>MilestoneApi</c>/<c>LessonApi.MistakeIds</c> thật —
/// mock-interface bỏ qua hẳn bước này, không chứng minh được item 3 (mở rộng DTO) hoạt động.</para>
/// </summary>
public class RoadmapMistakeNarrowMis1B5Tests
{
    private static int _orderCounter;

    /// <summary>Seed 1 buổi Scored + 1 tiêu chí YẾU (NeedsImprovement) + 1 answer dưới ngưỡng — đủ để
    /// <c>RoadmapMistakeLoader</c> trích đúng 1 lỗi, mint key "m1".</summary>
    private static Guid AddSessionWithWeakCriterion(
        TestDb t, Guid candidateId, RubricCriterion criterion, decimal baselinePercentage)
    {
        if (t.Db.RubricCriteria.Local.All(c => c.Id != criterion.Id)
            && !t.Db.RubricCriteria.Any(c => c.Id == criterion.Id))
            t.Db.RubricCriteria.Add(criterion);

        var session = TestDb.Session(candidateId, SessionStatus.Scored, JobCategory.BE);
        t.Db.PracticeSessions.Add(session);

        t.Db.Set<SessionCriterionScore>().Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            CriterionId = criterion.Id,
            CriterionName = criterion.Name,
            AverageScore = 1,
            MaxScore = criterion.MaxScore,
            Percentage = baselinePercentage,
            NeedsImprovement = true,
            CreatedAt = DateTime.UtcNow
        });

        AddMistakeAnswer(t, session.Id, criterion, score: 1, reasoning: "Không phân biệt được DI với Service Locator");
        return session.Id;
    }

    private static void AddMistakeAnswer(TestDb t, Guid sessionId, RubricCriterion criterion, decimal score, string reasoning)
    {
        var question = TestDb.Question(sessionId, order: ++_orderCounter);
        t.Db.PracticeQuestions.Add(question);
        var answer = TestDb.Answer(sessionId, question.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        answer.Transcript = "câu trả lời của ứng viên";
        t.Db.PracticeAnswers.Add(answer);

        t.Db.AnswerScores.Add(new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = criterion.Id,
            AttemptNo = 1,
            Score = score,
            Reasoning = reasoning,
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        });
    }

    private sealed class CaptureHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private static AiServiceRoadmapGenerator RealGenerator(string responseJson)
    {
        var http = new HttpClient(new CaptureHandler(responseJson)) { BaseAddress = new Uri("http://aiapi:8000") };
        return new AiServiceRoadmapGenerator(
            http, new ConfigurationBuilder().Build(), NullLogger<AiServiceRoadmapGenerator>.Instance);
    }

    // ═══════════ Test 1 — AI trả id BỊA lẫn id THẬT ⇒ chỉ id THẬT được lưu ═══════════

    [Fact]
    public async Task Create_AiTraIdBiaLanIdThat_ChiLuuIdThatVaoMistakeRefs()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        var sid = AddSessionWithWeakCriterion(t, candidateId, crit, baselinePercentage: 20);
        await t.Db.SaveChangesAsync();

        // "m1" là key THẬT (RoadmapMistakeLoader mint đúng key này cho lỗi vừa seed); "m99" BỊA —
        // chưa từng được cấp/trích, không tồn tại ở bất kỳ nguồn nào.
        const string json = """
            {"milestones":[{"title":"M1","focusCriteria":[],"lessons":[{"title":"L1","mistakeIds":["m1","m99"]}],"mistakeIds":["m1","m99"]}]}
            """;
        var svc = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, RealGenerator(json), NullLogger<RoadmapService>.Instance);

        var res = await svc.CreateAsync(
            candidateId,
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid]),
            default);

        var milestone = await t.NewContext().RoadmapMilestones.AsNoTracking()
            .SingleAsync(m => m.RoadmapId == res.Id);
        Assert.Equal(["m1"], milestone.MistakeRefs);

        var lesson = await t.NewContext().RoadmapLessons.AsNoTracking()
            .SingleAsync(l => l.MilestoneId == milestone.Id);
        Assert.Equal(["m1"], lesson.MistakeRefs);

        // roadmap_mistakes chỉ chứa lỗi THẬT SỰ ĐÃ TRÍCH (m1) — "m99" không được ghi thêm vào đây,
        // narrow chỉ LỌC id tham chiếu, KHÔNG tạo hàng roadmap_mistakes mới từ id AI bịa.
        var saved = await t.NewContext().RoadmapMistakes.AsNoTracking()
            .Where(m => m.RoadmapId == res.Id).ToListAsync();
        Assert.Equal(["m1"], saved.Select(m => m.MistakeKey));

        // Response cũng phản ánh đúng: 1 lỗi (không phải 2 — "m99" không được đếm).
        Assert.Equal(1, res.Milestones.Single().MistakeCount);
    }

    // ═══════════ Test 2 — AI trả ĐÚNG id thật (raw JSON) ⇒ deserialize + lưu đúng ═══════════

    /// <summary>
    /// Hai lỗi ĐỀU thật (m1, m2) — dùng raw JSON (không mock interface) để buộc đường deserialize
    /// System.Text.Json thật sự chạy qua <c>MilestoneApi.MistakeIds</c>/<c>LessonApi.MistakeIds</c>
    /// (item 3 của MIS1-B5). 2 phần tử (không phải 1) để loại khả năng bug "chỉ đọc phần tử đầu".
    /// </summary>
    [Fact]
    public async Task Create_AiTraHaiIdThat_DeserializeVaLuuDungCaHai()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        var sid = AddSessionWithWeakCriterion(t, candidateId, crit, baselinePercentage: 10);
        // Answer thứ 2 dưới ngưỡng, cùng tiêu chí/buổi ⇒ RoadmapMistakeLoader mint thêm "m2".
        AddMistakeAnswer(t, sid, crit, score: 2, reasoning: "Nhầm lẫn giữa constructor injection và property injection");
        await t.Db.SaveChangesAsync();

        const string json = """
            {"milestones":[{"title":"M1","focusCriteria":[],"lessons":[{"title":"L1","mistakeIds":["m1","m2"]}],"mistakeIds":["m1","m2"]}]}
            """;
        var svc = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, RealGenerator(json), NullLogger<RoadmapService>.Instance);

        var res = await svc.CreateAsync(
            candidateId,
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid]),
            default);

        var milestone = await t.NewContext().RoadmapMilestones.AsNoTracking()
            .SingleAsync(m => m.RoadmapId == res.Id);
        Assert.Equal(["m1", "m2"], milestone.MistakeRefs);

        var lesson = await t.NewContext().RoadmapLessons.AsNoTracking()
            .SingleAsync(l => l.MilestoneId == milestone.Id);
        Assert.Equal(["m1", "m2"], lesson.MistakeRefs);

        Assert.Equal(2, res.Milestones.Single().MistakeCount);
    }

    // ═══════════ Test 3 — mở lesson HAI LẦN ⇒ Mistakes vẫn có ở lần MỞ THỨ HAI ═══════════

    /// <summary>
    /// <c>MapLesson</c> có BA call site trong <c>RoadmapLessonService</c> — wire chỉ nhánh "vừa
    /// sinh xong" là kiểu lỗi dễ lọt nhất: test mở MỘT LẦN sẽ xanh dù nhánh "mở lại" (đường phổ
    /// biến nhất — lý thuyết đã sinh, đọc thẳng DB) vẫn trả <c>Mistakes = null</c>.
    /// </summary>
    [Fact]
    public async Task OpenLesson_MoHaiLan_MistakesVanCoODungMoThuHai()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        t.Db.RubricCriteria.Add(crit);

        var roadmapId = Guid.NewGuid();
        var roadmap = new Roadmap
        {
            Id = roadmapId,
            CandidateId = candidateId,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            Language = "vi",
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "M1",
            FocusCriteria = ["Clarity"],
            Status = MilestoneStatus.Pending,
            MistakeRefs = ["m1"]
        };
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "L1",
            Status = LessonStatus.Theory,
            TheoryContent = null
        };
        milestone.Lessons.Add(lesson);
        roadmap.Milestones.Add(milestone);
        t.Db.Set<Roadmap>().Add(roadmap);

        t.Db.Set<RoadmapMistake>().Add(new RoadmapMistake
        {
            Id = Guid.NewGuid(),
            RoadmapId = roadmapId,
            MistakeKey = "m1",
            CriterionId = crit.Id,
            CriterionName = "Clarity",
            Question = "Giải thích dependency injection?",
            Answer = "câu trả lời sai của ứng viên",
            Reasoning = "Không phân biệt được DI với Service Locator",
            ScorePct = 20m,
            ThresholdPct = 50m,
            CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .ReturnsAsync(new LessonTheoryResult(
                "## Lý thuyết\n\nNội dung bài giảng", [], null,
                [new LessonMistakeReviewItem("m1", "Sai vì lẫn DI với Service Locator", "Đọc lại định nghĩa DI và phân biệt với anti-pattern")]));

        var svc = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen.Object, NullLogger<RoadmapLessonService>.Instance);

        // LẦN MỞ ĐẦU — sinh lý thuyết, lưu MistakeReview.
        var first = await svc.OpenLessonAsync(candidateId, roadmapId, lesson.Id, default);
        Assert.NotNull(first.Mistakes);
        Assert.Equal("m1", Assert.Single(first.Mistakes!).MistakeId);

        // LẦN MỞ THỨ HAI — HasUsableTheory=true ⇒ đọc thẳng DB, KHÔNG gọi AI lần nữa. Đây là nhánh
        // dễ bị bỏ sót nhất nếu chỉ wire MapLesson ở nhánh "vừa sinh".
        var second = await svc.OpenLessonAsync(candidateId, roadmapId, lesson.Id, default);
        Assert.NotNull(second.Mistakes);
        var item = Assert.Single(second.Mistakes!);
        Assert.Equal("m1", item.MistakeId);
        Assert.Equal("Sai vì lẫn DI với Service Locator", item.WhatWentWrong);
        Assert.Equal("Đọc lại định nghĩa DI và phân biệt với anti-pattern", item.HowToFixIt);

        // Idempotent — AI chỉ được gọi ĐÚNG 1 lần cho 2 lượt mở.
        gen.Verify(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<RoadmapMistake>?>()),
            Times.Once);
    }

    // ═══════════ Test 7 — refs KHÔNG khớp gì cả ⇒ mistakes=null, KHÔNG ném lỗi tuỳ tiện ═══════════

    /// <summary>
    /// <c>milestone.MistakeRefs</c> mang key "m99" nhưng KHÔNG có hàng <c>roadmap_mistakes</c> nào
    /// khớp (bảng RỖNG) — <c>LoadLessonMistakesAsync</c> phải trả rỗng rồi được gộp về <c>null</c>
    /// TRƯỚC khi gửi generator, KHÔNG ném lỗi/exception tuỳ tiện nào (bài học vẫn mở được bình
    /// thường, chỉ đơn giản không có mục lỗi nào để bám).
    /// </summary>
    [Fact]
    public async Task OpenLesson_RefsKhongKhopRoadmapMistakeNao_GuiMistakesNull_KhongNemLoi()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var roadmapId = Guid.NewGuid();
        var roadmap = new Roadmap
        {
            Id = roadmapId,
            CandidateId = candidateId,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            Language = "vi",
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "M1",
            FocusCriteria = ["Clarity"],
            Status = MilestoneStatus.Pending,
            MistakeRefs = ["m99"]   // KHÔNG có hàng roadmap_mistakes nào khớp key này.
        };
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "L1",
            Status = LessonStatus.Theory,
            TheoryContent = null
        };
        milestone.Lessons.Add(lesson);
        roadmap.Milestones.Add(milestone);
        t.Db.Set<Roadmap>().Add(roadmap);
        // CỐ Ý không seed RoadmapMistake nào — bảng roadmap_mistakes rỗng.
        await t.Db.SaveChangesAsync();

        IReadOnlyList<RoadmapMistake>? seenMistakes = null;
        var called = false;
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .Callback<string, string, string, IReadOnlyList<string>, IReadOnlyList<string>?, IReadOnlyList<GroundingChunk>?, IReadOnlyList<CriterionEvidence>?, RoadmapMode, CancellationToken, IReadOnlyList<RoadmapMistake>?>(
                (_, _, _, _, _, _, _, _, _, mistakes) => { called = true; seenMistakes = mistakes; })
            .ReturnsAsync(new LessonTheoryResult("## Lý thuyết\n\nNội dung", [], null, null));

        var svc = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen.Object, NullLogger<RoadmapLessonService>.Instance);

        var ex = await Record.ExceptionAsync(() => svc.OpenLessonAsync(candidateId, roadmapId, lesson.Id, default));

        Assert.Null(ex);
        Assert.True(called);
        Assert.Null(seenMistakes);
    }
}
