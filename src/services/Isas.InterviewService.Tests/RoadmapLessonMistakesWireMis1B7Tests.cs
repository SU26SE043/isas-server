using System.Text.Json;
using System.Text.Json.Serialization;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// MIS1-B7 — <c>LessonResponse.Mistakes</c> phải serialize ĐÚNG hợp đồng ra client: 8 khoá
/// camelCase (id/criterionName/scorePct/question/answer/whatWentWrong/howToFixIt/sampleAnswer),
/// KHÔNG phải shape nội bộ <see cref="LessonMistakeReviewItem"/> (3 khoá, khoá đầu là
/// <c>mistakeId</c>) mà bản B4/B5 lỡ cắm nhầm vào <see cref="LessonResponse"/>.
///
/// <para>T1 SERIALIZE THẬT bằng đúng <see cref="JsonSerializerOptions"/> mà
/// <c>Program.cs:251-259</c> đăng ký cho controller (Web defaults + <see cref="JsonStringEnumConverter"/>
/// + <see cref="UtcDateTimeConverter"/>) — không assert trên object C#, vì lỗi gốc chỉ lộ ra ở
/// tầng JSON (namespace khác nhau giữa 2 record trùng số trường một phần).</para>
/// </summary>
public class RoadmapLessonMistakesWireMis1B7Tests
{
    private static JsonSerializerOptions BuildControllerJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcDateTimeConverter());
        return options;
    }

    private static Roadmap BuildRoadmap(Guid candidateId, Guid roadmapId)
        => new()
        {
            Id = roadmapId,
            CandidateId = candidateId,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            Language = "vi",
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

    private static RoadmapMistake Seed(Guid roadmapId, string key, string criterionName, Guid criterionId)
        => new()
        {
            Id = Guid.NewGuid(),
            RoadmapId = roadmapId,
            MistakeKey = key,
            CriterionId = criterionId,
            CriterionName = criterionName,
            Question = $"Câu hỏi của {key}",
            Answer = $"Câu trả lời của {key}",
            Reasoning = $"Lý do sai của {key}",
            SampleAnswer = $"Đáp án mẫu của {key}",
            ScorePct = 20m,
            ThresholdPct = 50m,
            CreatedAt = DateTime.UtcNow
        };

    // ═══════════════════════ T1 — hợp đồng JSON: đúng 8 khoá camelCase ═══════════════════════

    [Fact]
    public void Serialize_LessonResponse_MistakeItem_CoDungTamKhoaCamelCase()
    {
        var item = new LessonMistakeResponse(
            Id: "m1",
            CriterionName: "Clarity",
            ScorePct: 20m,
            Question: "Giải thích dependency injection?",
            Answer: "câu trả lời sai",
            WhatWentWrong: "Sai vì lẫn DI với Service Locator",
            HowToFixIt: "Đọc lại định nghĩa DI",
            SampleAnswer: "DI là...");

        var lesson = new LessonResponse(
            Id: Guid.NewGuid(), OrderNo: 1, Title: "L1", TheoryContent: "## Lý thuyết",
            SessionId: null, Status: "Theory", Resources: [], Citations: null,
            AttemptCount: 0, CanRetry: false, Mistakes: [item]);

        var json = JsonSerializer.Serialize(lesson, BuildControllerJsonOptions());
        using var doc = JsonDocument.Parse(json);

        var mistakesEl = doc.RootElement.GetProperty("mistakes");
        Assert.Equal(JsonValueKind.Array, mistakesEl.ValueKind);
        var first = Assert.Single(mistakesEl.EnumerateArray());

        var keys = first.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var expectedKeys = new[]
        {
            "answer", "criterionName", "howToFixIt", "id",
            "question", "sampleAnswer", "scorePct", "whatWentWrong"
        }.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedKeys, keys);

        Assert.Equal("m1", first.GetProperty("id").GetString());
        Assert.Equal("Clarity", first.GetProperty("criterionName").GetString());
        Assert.Equal(20m, first.GetProperty("scorePct").GetDecimal());
        Assert.Equal("Giải thích dependency injection?", first.GetProperty("question").GetString());
        Assert.Equal("câu trả lời sai", first.GetProperty("answer").GetString());
        Assert.Equal("Sai vì lẫn DI với Service Locator", first.GetProperty("whatWentWrong").GetString());
        Assert.Equal("Đọc lại định nghĩa DI", first.GetProperty("howToFixIt").GetString());
        Assert.Equal("DI là...", first.GetProperty("sampleAnswer").GetString());

        // KHÔNG được có khoá "mistakeId" — đó là shape hợp đồng dây nội bộ (LessonMistakeReviewItem),
        // đúng lỗi ĐANG SỬA (BE trả nhầm 3 khoá thay vì 8, khoá đầu tên "mistakeId" chứ không phải "id").
        Assert.False(first.TryGetProperty("mistakeId", out _));
    }

    // ═══════════════════════ T2 — nhánh ĐỌC LẠI cũng phải trả mistakes ═══════════════════════

    [Fact]
    public async Task OpenLesson_DaCoLyThuyet_DocLai_VanTraMistakesDayDu()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        t.Db.RubricCriteria.Add(crit);

        var roadmapId = Guid.NewGuid();
        var roadmap = BuildRoadmap(candidateId, roadmapId);
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
            // Lý thuyết ĐÃ DÙNG ĐƯỢC (có xuống dòng) → OpenLessonAsync đi nhánh đọc lại, KHÔNG gọi AI.
            TheoryContent = "## Lý thuyết\n\nNội dung đã sinh từ trước",
            MistakeReview = [new LessonMistakeReviewItem("m1", "Sai vì lẫn DI", "Đọc lại định nghĩa DI")]
        };
        milestone.Lessons.Add(lesson);
        roadmap.Milestones.Add(milestone);
        t.Db.Set<Roadmap>().Add(roadmap);
        t.Db.Set<RoadmapMistake>().Add(Seed(roadmapId, "m1", "Clarity", crit.Id));
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceRoadmapGenerator>(MockBehavior.Strict);
        var svc = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen.Object, NullLogger<RoadmapLessonService>.Instance);

        var res = await svc.OpenLessonAsync(candidateId, roadmapId, lesson.Id, default);

        Assert.NotNull(res.Mistakes);
        var item = Assert.Single(res.Mistakes!);
        Assert.Equal("m1", item.Id);
        Assert.Equal("Clarity", item.CriterionName);
        Assert.Equal("Câu hỏi của m1", item.Question);
        Assert.Equal("Câu trả lời của m1", item.Answer);
        Assert.Equal("Đáp án mẫu của m1", item.SampleAnswer);
        Assert.Equal("Sai vì lẫn DI", item.WhatWentWrong);
        Assert.Equal("Đọc lại định nghĩa DI", item.HowToFixIt);

        // Strict mock: gen.Object không setup gì — gọi bất kỳ method nào cũng ném ngay, nên tới
        // được đây là bằng chứng AI KHÔNG bị gọi ở nhánh đọc lại.
    }

    // ═══════════════════════ T3 — MistakeReview null vẫn đủ 8 khoá, 2 trường cuối null ═══════════════════════

    [Fact]
    public async Task OpenLesson_MistakeReviewNull_VanTraDuTruongConLai_WhatWentWrongHowToFixItNull()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        t.Db.RubricCriteria.Add(crit);

        var roadmapId = Guid.NewGuid();
        var roadmap = BuildRoadmap(candidateId, roadmapId);
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
            Id = Guid.NewGuid(), OrderNo = 1, Title = "L1", Status = LessonStatus.Theory, TheoryContent = null
        };
        milestone.Lessons.Add(lesson);
        roadmap.Milestones.Add(milestone);
        t.Db.Set<Roadmap>().Add(roadmap);
        t.Db.Set<RoadmapMistake>().Add(Seed(roadmapId, "m1", "Clarity", crit.Id));
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            // MistakeReview: null — model bản cũ / không gửi mistakes cho lượt này.
            .ReturnsAsync(new LessonTheoryResult("## Lý thuyết\n\nNội dung", [], null, null));

        var svc = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen.Object, NullLogger<RoadmapLessonService>.Instance);

        var res = await svc.OpenLessonAsync(candidateId, roadmapId, lesson.Id, default);

        Assert.NotNull(res.Mistakes);
        var item = Assert.Single(res.Mistakes!);
        Assert.Equal("m1", item.Id);
        Assert.Equal("Clarity", item.CriterionName);
        Assert.Equal(20m, item.ScorePct);
        Assert.Equal("Câu hỏi của m1", item.Question);
        Assert.Equal("Câu trả lời của m1", item.Answer);
        Assert.Equal("Đáp án mẫu của m1", item.SampleAnswer);
        Assert.Null(item.WhatWentWrong);
        Assert.Null(item.HowToFixIt);
    }

    // ═══════════════════════ T4 — refs rỗng ⇒ Mistakes = null (không phải []) ═══════════════════════

    [Fact]
    public async Task OpenLesson_KhongCoMistakeRefsNao_MistakesLaNull()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var roadmapId = Guid.NewGuid();
        var roadmap = BuildRoadmap(candidateId, roadmapId);
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(), OrderNo = 1, Title = "M1", FocusCriteria = ["Clarity"],
            Status = MilestoneStatus.Pending, MistakeRefs = null
        };
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(), OrderNo = 1, Title = "L1", Status = LessonStatus.Theory,
            TheoryContent = null, MistakeRefs = null
        };
        milestone.Lessons.Add(lesson);
        roadmap.Milestones.Add(milestone);
        t.Db.Set<Roadmap>().Add(roadmap);
        // CỐ Ý không seed RoadmapMistake nào.
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .ReturnsAsync(new LessonTheoryResult("## Lý thuyết\n\nNội dung", [], null, null));

        var svc = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen.Object, NullLogger<RoadmapLessonService>.Instance);

        var res = await svc.OpenLessonAsync(candidateId, roadmapId, lesson.Id, default);

        Assert.Null(res.Mistakes);
    }

    // ═══════════════════════ T5 — thứ tự lấy lỗi: Take(3) phải giữ m1/m2/m3, KHÔNG PHẢI m10/m11/m12 ═══════════════════════

    [Fact]
    public async Task OpenLesson_ThuTuLayLoi_Take3_LayDungM1M2M3_KhongPhaiM10M11M12()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        t.Db.RubricCriteria.Add(crit);

        var roadmapId = Guid.NewGuid();
        var roadmap = BuildRoadmap(candidateId, roadmapId);
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(),
            OrderNo = 1,
            Title = "M1",
            FocusCriteria = ["Clarity"],
            Status = MilestoneStatus.Pending,
            // Fallback milestone-level refs mang 6 key, VƯỢT trần Take(3) — đây là điều kiện DUY
            // NHẤT khiến thứ tự sắp có ý nghĩa (với ≤3 key, Take(3) giữ nguyên tất cả bất kể sắp
            // sao). Sắp CHUỖI (bug cũ) ra "m1","m10","m11","m12","m2","m3" → Take(3) = m1/m10/m11
            // (SAI). Sắp SỐ (đã sửa) ra m1..m12 tăng dần → Take(3) = m1/m2/m3 (ĐÚNG).
            MistakeRefs = ["m1", "m2", "m3", "m10", "m11", "m12"]
        };
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(), OrderNo = 1, Title = "L1", Status = LessonStatus.Theory, TheoryContent = null
        };
        milestone.Lessons.Add(lesson);
        roadmap.Milestones.Add(milestone);
        t.Db.Set<Roadmap>().Add(roadmap);
        foreach (var key in new[] { "m1", "m2", "m3", "m10", "m11", "m12" })
            t.Db.Set<RoadmapMistake>().Add(Seed(roadmapId, key, "Clarity", crit.Id));
        await t.Db.SaveChangesAsync();

        IReadOnlyList<RoadmapMistake>? seenMistakes = null;
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .Callback<string, string, string, IReadOnlyList<string>, IReadOnlyList<string>?, IReadOnlyList<GroundingChunk>?, IReadOnlyList<CriterionEvidence>?, RoadmapMode, CancellationToken, IReadOnlyList<RoadmapMistake>?>(
                (_, _, _, _, _, _, _, _, _, mistakes) => seenMistakes = mistakes)
            .ReturnsAsync(new LessonTheoryResult("## Lý thuyết\n\nNội dung", [], null, null));

        var svc = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen.Object, NullLogger<RoadmapLessonService>.Instance);

        var res = await svc.OpenLessonAsync(candidateId, roadmapId, lesson.Id, default);

        Assert.NotNull(res.Mistakes);
        Assert.Equal(["m1", "m2", "m3"], res.Mistakes!.Select(m => m.Id));

        // Cùng tập đó cũng phải là thứ gửi AI (mistakesForLesson dùng chung cho cả response lẫn
        // /generate-lesson-theory — MapLesson và mistakesForLesson KHÔNG được lệch nguồn).
        Assert.NotNull(seenMistakes);
        Assert.Equal(["m1", "m2", "m3"], seenMistakes!.Select(m => m.MistakeKey));
    }
}
