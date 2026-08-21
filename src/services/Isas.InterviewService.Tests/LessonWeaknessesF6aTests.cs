using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// F6a — bài học roadmap phải nói đúng chỗ ứng viên đang yếu.
///
/// Bug: `OpenLessonAsync` luôn truyền `weaknesses: null` ⇒ nhánh `if weaknesses:` trong prompt của
/// AIService là code CHẾT. Đường ống đã thông sẵn từ interface tới prompt — thiếu đúng mỗi dữ liệu,
/// nên không có gì lỗi, bài học chỉ đơn giản là viết chung chung.
/// </summary>
public class LessonWeaknessesF6aTests
{
    private static Roadmap SeedRoadmap(
        TestDb t, Guid candidateId, Dictionary<string, decimal>? baseline, params string[] focus)
    {
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow,
            Baseline = baseline,
            Milestones =
            [
                new RoadmapMilestone
                {
                    Id = Guid.NewGuid(),
                    OrderNo = 1,
                    Title = "Milestone 1",
                    FocusCriteria = focus.ToList(),
                    Status = MilestoneStatus.Pending,
                    Lessons =
                    [
                        new RoadmapLesson
                        {
                            Id = Guid.NewGuid(), OrderNo = 1,
                            Title = "Lesson 1", Status = LessonStatus.Theory
                        }
                    ]
                }
            ]
        };
        t.Db.Roadmaps.Add(roadmap);
        t.Db.SaveChanges();
        return roadmap;
    }

    /// <summary>Mở lesson đầu tiên; trả về `weaknesses` mà service thực sự gửi xuống AIService.</summary>
    private static async Task<IReadOnlyList<string>?> CaptureWeaknessesAsync(
        TestDb t, Roadmap roadmap, Guid candidateId, decimal? threshold = null)
    {
        IReadOnlyList<string>? captured = null;
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<string>, IReadOnlyList<string>?, IReadOnlyList<GroundingChunk>?, IReadOnlyList<CriterionEvidence>?, CancellationToken>(
                (_, _, _, _, weaknesses, _, _, _) => captured = weaknesses)
            .ReturnsAsync(new LessonTheoryResult("## Lý thuyết", []));

        var options = threshold is null
            ? null
            : Options.Create(new ScoringOptions { ImprovementThresholdPct = threshold.Value });

        var svc = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen.Object,
            NullLogger<RoadmapLessonService>.Instance, options);

        var lesson = roadmap.Milestones.First().Lessons.First();
        await svc.OpenLessonAsync(candidateId, roadmap.Id, lesson.Id);
        return captured;
    }

    // ── Dưới ngưỡng ∩ focus → gửi đúng danh sách ────────────────────────────
    [Fact]
    public async Task WeakCriteriaWithinFocus_AreSentToAi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var roadmap = SeedRoadmap(
            t, candidate,
            new Dictionary<string, decimal> { ["Clarity"] = 30m, ["Depth"] = 80m },
            "Clarity", "Depth");

        var weaknesses = await CaptureWeaknessesAsync(t, roadmap, candidate);

        // Chỉ Clarity yếu (30 < 50); Depth 80 là điểm MẠNH, đưa vào sẽ dạy nhầm trọng tâm.
        Assert.NotNull(weaknesses);
        Assert.Equal(["Clarity: 30%"], weaknesses);
    }

    // ── Điểm yếu NGOÀI focus của bài học này → bỏ qua (không dạy lạc đề) ────
    [Fact]
    public async Task WeakCriteriaOutsideFocus_AreIgnored()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var roadmap = SeedRoadmap(
            t, candidate,
            new Dictionary<string, decimal> { ["Clarity"] = 20m, ["Teamwork"] = 10m },
            "Clarity");

        var weaknesses = await CaptureWeaknessesAsync(t, roadmap, candidate);

        Assert.Equal(["Clarity: 20%"], weaknesses);
    }

    // ── Chưa có baseline (roadmap lập khi chưa luyện buổi nào) → null như cũ ──
    [Fact]
    public async Task NullBaseline_SendsNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var roadmap = SeedRoadmap(t, candidate, baseline: null, "Clarity");

        Assert.Null(await CaptureWeaknessesAsync(t, roadmap, candidate));
    }

    // ── Mọi tiêu chí đều TRÊN ngưỡng → null (không bịa điểm yếu) ────────────
    [Fact]
    public async Task AllAboveThreshold_SendsNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var roadmap = SeedRoadmap(
            t, candidate,
            new Dictionary<string, decimal> { ["Clarity"] = 70m, ["Depth"] = 90m },
            "Clarity", "Depth");

        Assert.Null(await CaptureWeaknessesAsync(t, roadmap, candidate));
    }

    // ── Đúng biên: pct == ngưỡng KHÔNG phải điểm yếu (khớp BC9 dùng "<") ────
    [Fact]
    public async Task ExactlyAtThreshold_IsNotAWeakness()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var roadmap = SeedRoadmap(
            t, candidate, new Dictionary<string, decimal> { ["Clarity"] = 50m }, "Clarity");

        Assert.Null(await CaptureWeaknessesAsync(t, roadmap, candidate));
    }

    // ── Ngưỡng cấu hình được (dùng chung ScoringOptions với BC9) ────────────
    [Fact]
    public async Task ThresholdIsConfigurable()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var roadmap = SeedRoadmap(
            t, candidate, new Dictionary<string, decimal> { ["Clarity"] = 65m }, "Clarity");

        // Ngưỡng mặc định 50 → 65 không phải điểm yếu; nâng lên 70 thì có.
        Assert.Null(await CaptureWeaknessesAsync(t, roadmap, candidate));

        using var t2 = new TestDb();
        var roadmap2 = SeedRoadmap(
            t2, candidate, new Dictionary<string, decimal> { ["Clarity"] = 65m }, "Clarity");
        Assert.Equal(
            ["Clarity: 65%"],
            await CaptureWeaknessesAsync(t2, roadmap2, candidate, threshold: 70m));
    }
}
