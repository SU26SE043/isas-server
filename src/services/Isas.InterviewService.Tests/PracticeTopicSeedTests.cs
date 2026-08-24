using Isas.InterviewService.Data;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Tests;

/// <summary>
/// TOP1-B2 — danh mục chủ đề luyện tập B2C.
///
/// Test QUAN TRỌNG NHẤT của bước này: <see cref="EveryTopic_CriterionName_MatchesAWhenTargetedRubricCriterion"/>.
/// Nó là thứ DUY NHẤT bắt được lỗi gõ sai tên tiêu chí trong seed — gõ sai làm phép lọc
/// chấm-theo-phạm-vi (INT-18) khớp 0 tiêu chí một cách CÂM LẶNG, không ném lỗi nào.
///
/// Viết TRƯỚC khi soạn seed (chạy PASS với <c>Build()</c> rỗng — vacuously true), rồi chạy lại
/// SAU khi soạn seed để nó thật sự kiểm tra nội dung.
/// </summary>
public class PracticeTopicSeedTests
{
    private static readonly JobCategory[] AllCategories =
        [JobCategory.BA, JobCategory.BE, JobCategory.FE];

    private static readonly string[] AllLanguages = ["vi", "en"];

    private static readonly string[] AllSeniorities = ["Fresher", "Junior", "Middle", "Senior"];

    // ── Test then-order 1: CriterionName phải khớp một tiêu chí WhenTargeted CÙNG (nghề, ngôn ngữ) ──
    [Fact]
    public void EveryTopic_CriterionName_MatchesAWhenTargetedRubricCriterion()
    {
        var topics = PracticeTopicSeed.Build();
        var rubric = B2CRubricSeed.Build();

        var whenTargetedByCategoryAndLanguage = rubric
            .Where(c => c.ScoringScope == ScoringScope.WhenTargeted)
            .Select(c => (c.JobCategory, c.Language, c.Name))
            .ToHashSet();

        foreach (var topic in topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.CriterionName));

            var key = (topic.JobCategory, topic.Language, topic.CriterionName!);
            Assert.True(
                whenTargetedByCategoryAndLanguage.Contains(key),
                $"Chủ đề '{topic.TopicKey}' ({topic.JobCategory}/{topic.Language}) trỏ tới tiêu chí " +
                $"'{topic.CriterionName}' — KHÔNG khớp bất kỳ tiêu chí WhenTargeted nào của " +
                $"({topic.JobCategory}, {topic.Language}) trong B2CRubricSeed.");
        }
    }

    // ── Idempotent: Build() hai lần ⇒ cùng tập Id; vi và en không đụng độ ────────────────────────
    [Fact]
    public void Seed_IsDeterministic_FixedIds_NoDuplicates_ViEnDoNotCollide()
    {
        var first = PracticeTopicSeed.Build();
        var second = PracticeTopicSeed.Build();

        Assert.Equal(
            first.Select(t => t.Id).OrderBy(x => x),
            second.Select(t => t.Id).OrderBy(x => x));

        Assert.Equal(first.Count, first.Select(t => t.Id).Distinct().Count());

        var viIds = first.Where(t => t.Language == "vi").Select(t => t.Id).ToHashSet();
        var enIds = first.Where(t => t.Language == "en").Select(t => t.Id).ToHashSet();
        Assert.Empty(viIds.Intersect(enIds));

        // Khớp UNIQUE (TopicKey, Language, Version) — không hai row nào trùng cả ba.
        Assert.Equal(
            first.Count,
            first.Select(t => (t.TopicKey, t.Language, t.Version)).Distinct().Count());
    }

    // ── Mỗi ô (nghề, cấp độ, ngôn ngữ) có ĐỦ 8 chủ đề ─────────────────────────────────────────────
    [Fact]
    public void EveryCell_JobCategorySeniorityLanguage_HasExactlyEightTopics()
    {
        var topics = PracticeTopicSeed.Build();

        foreach (var cat in AllCategories)
        foreach (var seniority in AllSeniorities)
        foreach (var language in AllLanguages)
        {
            var count = topics.Count(t =>
                t.JobCategory == cat && t.Seniority == seniority && t.Language == language);

            Assert.True(
                count == 8,
                $"({cat}, {seniority}, {language}) có {count} chủ đề, cần đúng 8.");
        }
    }

    [Fact]
    public void Seed_TotalRowCount_Is192()
    {
        Assert.Equal(192, PracticeTopicSeed.Build().Count);
    }
}
