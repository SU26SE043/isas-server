using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// J5 — <c>ScoringJob.Seniority</c> phải tới được prompt CHẤM, kèm van B2B (CAMP-10): buổi thuộc
/// campaign xếp hạng chung một bảng, không được chấm bằng hai thước khác nhau theo cấp độ. Khoá
/// bất biến này ở CẢ HAI đường publish (<see cref="AnswerService"/> lúc upload +
/// <see cref="StuckAnswerRepublisher"/> đường cứu hộ) — bỏ sót một đường là buổi đi đường cứu hộ
/// được chấm bằng thước khác buổi đi đường thường, hỏng âm thầm không lỗi nào nổ.
/// </summary>
public class SeniorityScoringGateTests
{
    // ═════════════════ (1) Đường publish lúc upload — AnswerService ═════════════════

    private static (AnswerService svc, Mock<IScoringJobPublisher> pub) Answering(TestDb t)
    {
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/x.webm");
        var pub = new Mock<IScoringJobPublisher>();
        var svc = new AnswerService(
            t.Db, storage.Object, pub.Object, new Mock<ISessionScoringNotifier>().Object,
            Options.Create(new ScoringOptions()), NullLogger<AnswerService>.Instance);
        return (svc, pub);
    }

    private static async Task<ScoringJob> UploadAndCapture(
        TestDb t, PracticeSession session, PracticeQuestion question)
    {
        var (svc, pub) = Answering(t);
        ScoringJob? captured = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);

        await svc.UploadAnswerAsync(
            session.Id, question.Id, session.CandidateId,
            new MemoryStream([1, 2, 3]), "audio/webm", 30);

        Assert.NotNull(captured);
        return captured!;
    }

    [Fact]
    public async Task Upload_B2CSession_JobCarriesSessionSeniority()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready, JobCategory.BE);
        session.Seniority = "Senior";
        var question = TestDb.Question(session.Id);
        t.Db.AddRange(session, question, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var job = await UploadAndCapture(t, session, question);

        Assert.Equal("Senior", job.Seniority);
    }

    // 🔒 Test quan trọng nhất của J5: buổi B2B mang `Seniority` khác mặc định trên session (chứng
    // minh guard không phải "tình cờ null" vì chưa ai set) — job PHẢI vẫn nhận null.
    [Fact]
    public async Task Upload_B2BSession_JobSeniorityIsNull_EvenWhenSessionHasSeniority()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Ready, JobCategory.BE, campaignId: campaignId);
        session.Seniority = "Senior";
        var question = TestDb.Question(session.Id);
        t.Db.AddRange(session, question, TestDb.Criterion(session.JobCategory, campaignId: campaignId));
        await t.Db.SaveChangesAsync();

        var job = await UploadAndCapture(t, session, question);

        Assert.Null(job.Seniority);
    }

    // ═════════════════ (2) Đường cứu hộ — StuckAnswerRepublisher ═════════════════

    private static async Task ScanOnce(StuckAnswerRepublisher r)
    {
        var mi = typeof(StuckAnswerRepublisher)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    private static (StuckAnswerRepublisher r, Mock<IScoringJobPublisher> pub) BuildRepublisher(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var pub = new Mock<IScoringJobPublisher>();
        var r = new StuckAnswerRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            Options.Create(new RepublisherSettings { BatchSize = 200 }),
            Options.Create(new ScoringOptions()),
            NullLogger<StuckAnswerRepublisher>.Instance);
        return (r, pub);
    }

    [Fact]
    public async Task Republish_B2CSession_JobCarriesSessionSeniority()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress, JobCategory.BE);
        session.Seniority = "Middle";
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        t.Db.AddRange(session, q, a, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var (r, pub) = BuildRepublisher(t);
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        Assert.Equal("Middle", published!.Seniority);
    }

    // 🔒 Cùng test quan trọng nhất, nhưng ở đường CỨU HỘ — chỗ dễ đánh rơi nhất vì projection là
    // một anonymous type riêng, tách hẳn khỏi entity `PracticeSession`.
    [Fact]
    public async Task Republish_B2BSession_JobSeniorityIsNull_EvenWhenSessionHasSeniority()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(
            Guid.NewGuid(), SessionStatus.InProgress, JobCategory.BE, campaignId: campaignId);
        session.Seniority = "Senior";
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        t.Db.AddRange(session, q, a, TestDb.Criterion(session.JobCategory, campaignId: campaignId));
        await t.Db.SaveChangesAsync();

        var (r, pub) = BuildRepublisher(t);
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        Assert.Null(published!.Seniority);
    }
}
