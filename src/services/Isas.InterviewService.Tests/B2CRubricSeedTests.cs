using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// BC11 — nguồn rubric B2C theo JobCategory (BA/BE/FE).
///
/// HasData seed chỉ áp cho Npgsql (migration/pipeline); test SQLite EnsureCreated KHÔNG tự seed
/// nên các test dưới NẠP TAY đúng bộ row canonical <see cref="B2CRubricSeed.Build"/> (= các row
/// migration InsertData) để mô phỏng trạng thái DB SAU khi seed, rồi kiểm hành vi.
/// </summary>
public class B2CRubricSeedTests
{
    private static readonly JobCategory[] AllCategories =
        [JobCategory.BA, JobCategory.BE, JobCategory.FE];

    private static async Task ApplySeedAsync(InterviewDbContext db)
    {
        db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await db.SaveChangesAsync();
    }

    // (a) Sau seed: mỗi nghề BA/BE/FE có rubric_criteria(campaign_id IS NULL, is_active), Σweight = 1.
    [Fact]
    public async Task AfterSeed_EachJobCategory_HasActiveB2CRubric_SummingToOne()
    {
        using var t = new TestDb();
        await ApplySeedAsync(t.Db);

        foreach (var cat in AllCategories)
        foreach (var language in new[] { "vi", "en" })
        {
            var rows = await t.Db.RubricCriteria.AsNoTracking()
                .Where(c => c.CampaignId == null && c.IsActive && c.JobCategory == cat && c.Language == language)
                .ToListAsync();

            Assert.NotEmpty(rows);                          // có nguồn tiêu chí cho nghề (INT-8)
            Assert.Equal(1.0m, rows.Sum(c => c.Weight));    // Σweight = 1 (INT-10)
            Assert.All(rows, c => Assert.True(c.MaxScore > 0));
            Assert.All(rows, c => Assert.Equal(B2CRubricSeed.RubricVersion, c.Version)); // 1 nghề 1 version
        }
    }

    // (b) Sau seed: session B2C upload answer -> CÓ publish job chấm (hết "không có tiêu chí active").
    [Theory]
    [InlineData(JobCategory.BA)]
    [InlineData(JobCategory.BE)]
    [InlineData(JobCategory.FE)]
    public async Task AfterSeed_B2CUpload_PublishesScoringJob_WithSeededCriteria(JobCategory cat)
    {
        using var t = new TestDb();
        await ApplySeedAsync(t.Db);

        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready, cat: cat);   // campaign_id = null (B2C)
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        ScoringJob? published = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);

        var svc = BuildAnswerService(t, publisher);
        using var audio = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        // Publish job chấm PHẢI xảy ra — không còn bị bỏ vì "no active criteria".
        publisher.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(published);
        Assert.NotEmpty(published!.Criteria);
        Assert.All(published.Criteria, c => Assert.NotEqual(Guid.Empty, c.CriterionId));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == result.AnswerId);
        Assert.Equal(AnswerStatus.Scoring, saved.Status);   // publish OK -> Scoring
        Assert.NotNull(saved.LastScoringPublishedAt);
    }

    // (c1) Idempotent — seed dùng GUID CỐ ĐỊNH: Build() nhiều lần cho cùng tập Id, không trùng nội tại.
    [Fact]
    public void Seed_IsDeterministic_FixedIds_NoDuplicates()
    {
        var first = B2CRubricSeed.Build();
        var second = B2CRubricSeed.Build();

        Assert.Equal(
            first.Select(c => c.Id).OrderBy(x => x),
            second.Select(c => c.Id).OrderBy(x => x));                    // Id ổn định giữa các lần build

        Assert.Equal(first.Count, first.Select(c => c.Id).Distinct().Count());                    // không trùng Id
        Assert.Equal(first.Count, first.Select(c => (c.Language, c.JobCategory, c.Name)).Distinct().Count()); // không trùng (ngôn ngữ,nghề,tên)
        Assert.All(first, c => Assert.Null(c.CampaignId));               // đều là rubric B2C
        Assert.All(first, c => Assert.True(c.IsActive));
    }

    // (c2) Idempotent trên DB — seed lại lần 2 (khoá theo PK cố định như migration/HasData) KHÔNG nhân đôi.
    [Fact]
    public async Task Seed_ReAppliedToDb_DoesNotDuplicate()
    {
        using var t = new TestDb();
        await ApplySeedAsync(t.Db);
        var before = await t.Db.RubricCriteria.AsNoTracking().CountAsync(c => c.CampaignId == null);

        // Lần seed thứ 2: chỉ chèn row có Id CHƯA tồn tại (mô phỏng migration/HasData khoá theo PK cố định).
        using var ctx2 = t.NewContext();
        var existingIds = (await ctx2.RubricCriteria.AsNoTracking().Select(c => c.Id).ToListAsync()).ToHashSet();
        var toAdd = B2CRubricSeed.Build().Where(c => !existingIds.Contains(c.Id)).ToList();
        ctx2.RubricCriteria.AddRange(toAdd);
        await ctx2.SaveChangesAsync();

        var after = await ctx2.RubricCriteria.AsNoTracking().CountAsync(c => c.CampaignId == null);
        Assert.Empty(toAdd);              // Id cố định -> không có gì mới để thêm
        Assert.Equal(before, after);      // không nhân đôi
    }

    private static AnswerService BuildAnswerService(TestDb t, Mock<IScoringJobPublisher> publisher)
    {
        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new AnswerService(
            t.Db, storage.Object, publisher.Object, notifier.Object,
            TestDb.ScoringOpts(), NullLogger<AnswerService>.Instance);
    }
}
