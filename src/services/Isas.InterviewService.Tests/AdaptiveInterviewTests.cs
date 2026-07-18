using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

// Phỏng vấn THÍCH ỨNG — vòng lặp câu-kế-động ở AnswerService.UploadAnswerAsync:
//   transcribe đồng bộ (qua IAiServiceInterviewDecider) → quyết định → append 1 câu kế (frontier) →
//   đẩy transcript vào ScoringJob (worker bỏ Whisper). Mock decider (không gọi AIService thật).
public class AdaptiveInterviewTests
{
    // AnswerService KÈM decider (7 tham số). Adaptive chỉ chạy khi session.AdaptiveEnabled = true.
    private static AnswerService BuildAdaptive(
        TestDb t, Mock<IAiServiceInterviewDecider> decider,
        out Mock<IScoringJobPublisher> publisher, out List<ScoringJob> publishedJobs)
    {
        publisher = new Mock<IScoringJobPublisher>();
        var jobs = new List<ScoringJob>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => jobs.Add(j))
            .Returns(Task.CompletedTask);
        publishedJobs = jobs;

        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        return new AnswerService(
            t.Db, storage.Object, publisher.Object,
            new Mock<ISessionScoringNotifier>().Object, TestDb.ScoringOpts(),
            NullLogger<AnswerService>.Instance, decider.Object);
    }

    private static Mock<IAiServiceInterviewDecider> Decider(DecideNextResult result)
    {
        var d = new Mock<IAiServiceInterviewDecider>();
        d.Setup(x => x.DecideNextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<DecideTurnDto>>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<DecideCriterionDto>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return d;
    }

    private static PracticeSession AdaptiveSession(
        Guid candidate, Guid? campaignId = null, int maxQuestions = 10, int maxFollowUps = 3)
    {
        var s = TestDb.Session(candidate, SessionStatus.Ready, campaignId: campaignId);
        s.AdaptiveEnabled = true;
        s.MaxQuestions = maxQuestions;
        s.MaxFollowUps = maxFollowUps;
        return s;
    }

    // ── follow_up → append 1 câu (Kind=FollowUp, order+1, GeneratedFromAnswerId) + response mang câu kế ──
    [Fact]
    public async Task Frontier_FollowUp_AppendsQuestion_AndReturnsIt()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = AdaptiveSession(candidate);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "Bạn cho ví dụ cụ thể?", "transcript X", "cần đào sâu"));
        var svc = BuildAdaptive(t, decider, out _, out var jobs);

        using var audio = new MemoryStream(new byte[] { 1 });
        var result = await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        // Câu kế được append đúng shape.
        var qs = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(x => x.SessionId == session.Id).OrderBy(x => x.OrderNo).ToListAsync();
        Assert.Equal(2, qs.Count);
        var followUp = qs[1];
        Assert.Equal(QuestionKind.FollowUp, followUp.Kind);
        Assert.Equal(2, followUp.OrderNo);
        Assert.Equal(result.AnswerId, followUp.GeneratedFromAnswerId);
        Assert.Equal("Bạn cho ví dụ cụ thể?", followUp.Content);

        // Response mang câu kế + transcript + không complete.
        Assert.Equal("follow_up", result.NextAction);
        Assert.NotNull(result.NextQuestion);
        Assert.Equal(followUp.Id, result.NextQuestion!.Id);
        Assert.Equal("FollowUp", result.NextQuestion.Kind);
        Assert.False(result.InterviewComplete);
        Assert.Equal("transcript X", result.Transcript);

        // Transcript đồng bộ lưu lên answer + đẩy vào scoring job (worker bỏ Whisper).
        var savedAnswer = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == result.AnswerId);
        Assert.Equal("transcript X", savedAnswer.Transcript);
        Assert.All(jobs, j => Assert.Equal("transcript X", j.Transcript));
        Assert.NotEmpty(jobs);
    }

    // ── end → không append, InterviewComplete=true (vẫn lưu transcript) ─────────
    [Fact]
    public async Task Frontier_End_NoAppend_InterviewComplete()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = AdaptiveSession(candidate);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("end", null, "transcript cuối", "đủ độ phủ"));
        var svc = BuildAdaptive(t, decider, out _, out _);

        using var audio = new MemoryStream(new byte[] { 1 });
        var result = await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));   // không append
        Assert.True(result.InterviewComplete);
        Assert.Null(result.NextQuestion);
        Assert.Equal("end", result.NextAction);
        Assert.Equal("transcript cuối", result.Transcript);
    }

    // ── B2B seeds-first: chưa trả lời hết seed → KHÔNG gọi decider, không append ─
    [Fact]
    public async Task B2B_NotAllSeedsAnswered_DeciderNotCalled_NoAppend()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var session = AdaptiveSession(candidate, campaignId: campaignId);
        var q1 = TestDb.Question(session.Id, order: 1);
        var q2 = TestDb.Question(session.Id, order: 2);
        var crit = TestDb.Criterion(session.JobCategory, campaignId: campaignId);
        t.Db.AddRange(session, q1, q2, crit);
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "X?", "t", "r"));
        var svc = BuildAdaptive(t, decider, out _, out _);

        // Trả lời MỚI 1/2 seed → q2 còn pending → chưa tới frontier.
        using var audio = new MemoryStream(new byte[] { 1 });
        var result = await svc.UploadAnswerAsync(session.Id, q1.Id, candidate, audio, "audio/webm", 30);

        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<DecideTurnDto>>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<DecideCriterionDto>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));   // không thêm
        Assert.Null(result.NextQuestion);
        Assert.False(result.InterviewComplete);
    }

    // ── B2B seeds-first: trả lời hết seed → gọi decider → append (độc lập thứ tự) ─
    [Fact]
    public async Task B2B_AllSeedsAnswered_DeciderCalled_AppendsTail()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var session = AdaptiveSession(candidate, campaignId: campaignId);
        var q1 = TestDb.Question(session.Id, order: 1);
        var q2 = TestDb.Question(session.Id, order: 2);
        var crit = TestDb.Criterion(session.JobCategory, campaignId: campaignId);
        // q1 đã có answer sẵn; giờ trả lời q2 (seed cuối) → all-answered → append.
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        session.Status = SessionStatus.InProgress;
        t.Db.AddRange(session, q1, q2, crit, a1);
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("new_question", "Câu hỏi năng lực khác?", "t2", "r"));
        var svc = BuildAdaptive(t, decider, out _, out _);

        using var audio = new MemoryStream(new byte[] { 2 });
        var result = await svc.UploadAnswerAsync(session.Id, q2.Id, candidate, audio, "audio/webm", 30);

        Assert.Equal(3, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));   // +1 tail
        Assert.Equal("new_question", result.NextAction);
        Assert.NotNull(result.NextQuestion);
        Assert.Equal("NewQuestion", result.NextQuestion!.Kind);
    }

    // ── Hết ngân sách (MaxQuestions) → KHÔNG gọi decider, InterviewComplete=true ─
    [Fact]
    public async Task BudgetExhausted_DeciderNotCalled_InterviewComplete()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = AdaptiveSession(candidate, maxQuestions: 1);   // trần = 1 câu
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "X?", "t", "r"));
        var svc = BuildAdaptive(t, decider, out _, out _);

        using var audio = new MemoryStream(new byte[] { 1 });
        var result = await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<DecideTurnDto>>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<DecideCriterionDto>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(result.InterviewComplete);
        Assert.Null(result.NextQuestion);
        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));
    }

    // ── Decider ném → upload VẪN thành công, không append, transcript null, vẫn publish chấm (degrade tĩnh) ──
    [Fact]
    public async Task DeciderThrows_UploadSucceeds_StaticFallback()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = AdaptiveSession(candidate);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var decider = new Mock<IAiServiceInterviewDecider>();
        decider.Setup(x => x.DecideNextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<DecideTurnDto>>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<DecideCriterionDto>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /decide-next down"));
        var svc = BuildAdaptive(t, decider, out var publisher, out var jobs);

        using var audio = new MemoryStream(new byte[] { 1 });
        var result = await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        // Không append, không complete, transcript null (decide lỗi trước khi set) → worker sẽ transcribe async.
        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));
        Assert.Null(result.NextQuestion);
        Assert.False(result.InterviewComplete);
        Assert.Null(result.Transcript);

        // Answer vẫn lưu + vẫn publish chấm (job.Transcript null → worker Whisper như cũ).
        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == result.AnswerId);
        Assert.Equal(AnswerStatus.Scoring, saved.Status);
        publisher.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.All(jobs, j => Assert.Null(j.Transcript));
    }

    // ── Re-upload cùng frontier answer → CHỈ 1 câu con (idempotency), decider gọi đúng 1 lần ─
    [Fact]
    public async Task ReUploadFrontier_DoesNotDuplicateChild()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = AdaptiveSession(candidate);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "Ví dụ?", "t", "r"));

        // Mỗi upload = 1 request thật → context riêng (giống môi trường thật, tránh tái dùng entity track).
        async Task<UploadAnswerResult> UploadOn(Isas.InterviewService.ApplicationDbContext.InterviewDbContext db)
        {
            var storage = new Mock<IStorageService>();
            storage.Setup(s => s.UploadAsync(
                    It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("answer-audio/seed.webm");
            var svc = new AnswerService(db, storage.Object, new Mock<IScoringJobPublisher>().Object,
                new Mock<ISessionScoringNotifier>().Object, TestDb.ScoringOpts(),
                NullLogger<AnswerService>.Instance, decider.Object);
            using var audio = new MemoryStream(new byte[] { 1 });
            return await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);
        }

        await using (var c1 = t.NewContext()) await UploadOn(c1);   // lần 1 → append child
        await using (var c2 = t.NewContext()) await UploadOn(c2);   // lần 2 (re-upload) → KHÔNG append trùng

        Assert.Equal(2, await t.Db.PracticeQuestions.AsNoTracking().CountAsync(x => x.SessionId == session.Id));
        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<DecideTurnDto>>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<DecideCriterionDto>>(),
            It.IsAny<CancellationToken>()), Times.Once);   // chỉ lần 1 tới frontier
    }

    // ── Adaptive TẮT ở session → decider KHÔNG gọi dù có decider (regression flag) ─
    [Fact]
    public async Task AdaptiveDisabledOnSession_DeciderNotCalled()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);   // AdaptiveEnabled = false (mặc định)
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "X?", "t", "r"));
        var svc = BuildAdaptive(t, decider, out _, out _);

        using var audio = new MemoryStream(new byte[] { 1 });
        var result = await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<DecideTurnDto>>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<DecideCriterionDto>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));
        Assert.Null(result.NextQuestion);
        Assert.Null(result.NextAction);
    }
}
