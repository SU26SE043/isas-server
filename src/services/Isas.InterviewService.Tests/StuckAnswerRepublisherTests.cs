using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public class StuckAnswerRepublisherTests
{
    // Gọi ScanOnceAsync (private) một nhịp.
    private static async Task ScanOnce(StuckAnswerRepublisher r)
    {
        var mi = typeof(StuckAnswerRepublisher)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    // ServiceProvider thật để CreateScope() trả về DbContext dùng chung connection.
    private static (StuckAnswerRepublisher r, Mock<IScoringJobPublisher> pub) Build(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection));
        var provider = services.BuildServiceProvider();

        var pub = new Mock<IScoringJobPublisher>();
        var r = new StuckAnswerRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            NullLogger<StuckAnswerRepublisher>.Instance);
        return (r, pub);
    }

    private static async Task SeedActiveCriterion(TestDb t, JobCategory cat)
    {
        t.Db.Add(TestDb.Criterion(cat));
        await t.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task PublishHut_Uploaded_NullMarker_Old_IsRepublished_AndMarked()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        // Uploaded, chưa publish (null), upload 10 phút trước -> publish hụt.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Once);

        var saved = await t.NewContext().PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id);
        Assert.Equal(AnswerStatus.Scoring, saved.Status);          // đã dời sang Scoring
        Assert.NotNull(saved.LastScoringPublishedAt);              // mốc được set
    }

    // E1: republish job của session B2B mang ĐÚNG tiêu chí campaign (không phải rubric B2C cùng nghề).
    [Fact]
    public async Task PublishHut_B2BSession_RepublishesCampaignCriteria()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress, campaignId: campaignId);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        var campaignCrit = TestDb.Criterion(session.JobCategory, campaignId: campaignId, name: "Campaign-Crit");
        var b2cCrit = TestDb.Criterion(session.JobCategory, name: "B2C-Crit");
        t.Db.AddRange(session, q, a, campaignCrit, b2cCrit);
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
           .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        var crit = Assert.Single(published!.Criteria);
        Assert.Equal(campaignCrit.Id, crit.CriterionId);
    }

    // E9: re-publish cũng mang mức neo — tiêu chí không khai levels → dải mặc định 0..maxScore.
    [Fact]
    public async Task PublishHut_CriterionWithoutLevels_RepublishesDefaultBand()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);   // maxScore 5, không rubric_levels

        var (r, pub) = Build(t);
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
           .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        var c = Assert.Single(published!.Criteria);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, c.Levels.Select(l => l.Score).ToArray());
    }

    [Fact]
    public async Task FreshUpload_WithinGrace_NotRepublished()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        // Vừa upload 30s trước -> còn trong grace 2', request có thể đang chạy dở.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddSeconds(-30), lastPublished: null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Scoring_RecentlyPublished_NotRepublished()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        // Đang Scoring, mới publish 2 phút trước -> worker còn đang chấm, đừng đụng.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring,
            DateTime.UtcNow.AddMinutes(-20), lastPublished: DateTime.UtcNow.AddMinutes(-2));
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Scoring_LostLongAgo_IsRepublished()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        // Đang Scoring nhưng publish 20 phút trước, không thấy callback -> worker mất tích.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring,
            DateTime.UtcNow.AddMinutes(-40), lastPublished: DateTime.UtcNow.AddMinutes(-20));
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Once);

        var saved = await t.NewContext().PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id);
        Assert.True(saved.LastScoringPublishedAt > DateTime.UtcNow.AddMinutes(-1)); // mốc dời sang now
    }

    [Fact]
    public async Task Scored_NeverRepublished()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored,
            DateTime.UtcNow.AddMinutes(-60), lastPublished: DateTime.UtcNow.AddMinutes(-50));
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SessionNotActive_NotRepublished()
    {
        using var t = new TestDb();
        // Session Ready (chưa làm) -> answer cũ cũng không nên bị đẩy.
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-30), lastPublished: null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
