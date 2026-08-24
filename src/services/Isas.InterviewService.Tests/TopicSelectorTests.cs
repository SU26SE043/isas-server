using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Logging;

namespace Isas.InterviewService.Tests;

/// <summary>
/// TOP1-B3 — <see cref="TopicSelector"/>. Thuần hàm, không DB, không luồng tạo buổi — mọi test
/// dựng pool GIẢ trực tiếp (không cần <c>PracticeTopicSeed</c>/DbContext).
/// </summary>
public class TopicSelectorTests
{
    private static PracticeTopic Topic(string key, string? criterionName = null) => new()
    {
        Id = Guid.NewGuid(),
        TopicKey = key,
        JobCategory = JobCategory.BE,
        Seniority = "Middle",
        Language = "vi",
        Label = key,
        CriterionName = criterionName,
        DisplayOrder = 1,
        IsActive = true,
        Version = 1,
    };

    // ── pool 8 / slots 5 ⇒ đúng 5, không trùng Key — 200 seed ngẫu nhiên khác nhau ────────────────
    [Fact]
    public void Pool8_Slots5_NoTargetable_ReturnsExactly5Distinct_Across200Seeds()
    {
        var pool = Enumerable.Range(1, 8).Select(i => Topic($"t{i}")).ToList();
        var totalDuplicates = 0;

        for (var seed = 0; seed < 200; seed++)
        {
            var selector = new TopicSelector(new Random(seed));
            var result = selector.Select(JobCategory.BE, "Middle", "vi", 5, [], pool);

            Assert.Equal(5, result.Count);
            var distinctKeys = result.Select(t => t.TopicKey).Distinct().Count();
            totalDuplicates += 5 - distinctKeys;
        }

        Console.WriteLine($"[TOP1-B3] 200 seed x pool8/slots5 -> tong so lan trung Key = {totalDuplicates}");
        Assert.Equal(0, totalDuplicates);
    }

    // ── slots > pool ⇒ trả hết pool + LogWarning, không ném ───────────────────────────────────────
    [Fact]
    public void SlotsGreaterThanPool_ReturnsWholePool_LogsWarning_DoesNotThrow()
    {
        var pool = Enumerable.Range(1, 3).Select(i => Topic($"t{i}")).ToList();
        var logger = new CapturingLogger<TopicSelector>();
        var selector = new TopicSelector(new Random(1), logger);

        var result = selector.Select(JobCategory.BE, "Middle", "vi", 5, [], pool);

        Assert.Equal(3, result.Count);
        Assert.Equal(
            pool.Select(t => t.TopicKey).OrderBy(k => k),
            result.Select(t => t.TopicKey).OrderBy(k => k));
        Assert.Contains(LogLevel.Warning, logger.Levels);
    }

    // ── targetable 3 / slots 5 ⇒ tập CriterionName của kết quả ⊇ 3 tên đó, lặp nhiều seed ────────
    [Fact]
    public void Targetable3_Slots5_ResultCoversAllThree_AcrossManySeeds()
    {
        var pool = new List<PracticeTopic>
        {
            Topic("c1-a", "C1"), Topic("c1-b", "C1"),
            Topic("c2-a", "C2"), Topic("c2-b", "C2"),
            Topic("c3-a", "C3"), Topic("c3-b", "C3"),
            Topic("other-a", "C4"), Topic("other-b", null),
        };
        string[] targetable = ["C1", "C2", "C3"];

        for (var seed = 0; seed < 100; seed++)
        {
            var selector = new TopicSelector(new Random(seed));
            var result = selector.Select(JobCategory.BE, "Middle", "vi", 5, targetable, pool);

            Assert.Equal(5, result.Count);
            Assert.Equal(5, result.Select(t => t.TopicKey).Distinct().Count());

            var coveredCriteria = result.Select(t => t.CriterionName).ToHashSet();
            HashSet<string?> requiredSubset = ["C1", "C2", "C3"];
            Assert.Superset(requiredSubset, coveredCriteria);
        }
    }

    // ── slots 2 / targetable 3 ⇒ đúng 2 tiêu chí KHÁC NHAU, lặp nhiều seed ────────────────────────
    [Fact]
    public void Slots2_Targetable3_CoversExactlyTwoDistinctCriteria_AcrossManySeeds()
    {
        var pool = new List<PracticeTopic>
        {
            Topic("c1", "C1"),
            Topic("c2", "C2"),
            Topic("c3", "C3"),
        };
        string[] targetable = ["C1", "C2", "C3"];

        for (var seed = 0; seed < 100; seed++)
        {
            var selector = new TopicSelector(new Random(seed));
            var result = selector.Select(JobCategory.BE, "Middle", "vi", 2, targetable, pool);

            Assert.Equal(2, result.Count);
            var distinctCriteria = result.Select(t => t.CriterionName).Distinct().Count();
            Assert.Equal(2, distinctCriteria);
        }
    }

    // ── Random(42) ⇒ hai lần chạy ra danh sách GIỐNG HỆT ─────────────────────────────────────────
    [Fact]
    public void SameSeed42_TwoRuns_ProduceIdenticalOrderedResult()
    {
        var pool = new List<PracticeTopic>
        {
            Topic("c1-a", "C1"), Topic("c1-b", "C1"),
            Topic("c2-a", "C2"), Topic("c2-b", "C2"),
            Topic("c3-a", "C3"),
            Topic("other-a", "C4"), Topic("other-b", null), Topic("other-c", "C5"),
        };
        string[] targetable = ["C1", "C2", "C3"];

        var run1 = new TopicSelector(new Random(42)).Select(JobCategory.BE, "Middle", "vi", 5, targetable, pool);
        var run2 = new TopicSelector(new Random(42)).Select(JobCategory.BE, "Middle", "vi", 5, targetable, pool);

        Assert.Equal(run1.Select(t => t.TopicKey), run2.Select(t => t.TopicKey));
    }

    // ── pool rỗng ⇒ trả rỗng, không ném ────────────────────────────────────────────────────────────
    [Fact]
    public void EmptyPool_ReturnsEmpty_DoesNotThrow_LogsInformation()
    {
        var logger = new CapturingLogger<TopicSelector>();
        var selector = new TopicSelector(new Random(1), logger);

        var result = selector.Select(JobCategory.BE, "Middle", "vi", 3, ["X"], []);

        Assert.Empty(result);
        Assert.Contains(LogLevel.Information, logger.Levels);
    }

    // ── Ctor không truyền Random => Random.Shared, KHÔNG ném ─────────────────────────────────────
    [Fact]
    public void DefaultCtor_UsesRandomShared_DoesNotThrow()
    {
        var pool = Enumerable.Range(1, 5).Select(i => Topic($"t{i}")).ToList();
        var selector = new TopicSelector();

        var result = selector.Select(JobCategory.BE, "Middle", "vi", 3, [], pool);

        Assert.Equal(3, result.Count);
    }

    /// <summary>Fake ILogger tối giản — chỉ ghi lại LogLevel của mỗi lời gọi, không phụ thuộc Moq.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }
}
