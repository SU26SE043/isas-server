using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public class AnswerServiceTests
{
    private static AnswerService Build(
        TestDb t, Mock<IScoringJobPublisher> publisher, out Mock<IStorageService> storage)
        => Build(t, publisher, out storage, out _);

    private static AnswerService Build(
        TestDb t, Mock<IScoringJobPublisher> publisher, out Mock<IStorageService> storage,
        out Mock<ISessionScoringNotifier> scoringNotifier)
    {
        storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        scoringNotifier = new Mock<ISessionScoringNotifier>();
        scoringNotifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new AnswerService(
            t.Db, storage.Object, publisher.Object, scoringNotifier.Object,
            NullLogger<AnswerService>.Instance);
    }

    [Fact]
    public async Task Upload_PublishSucceeds_AnswerBecomesScoring_AndMarkerSet()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        var svc = Build(t, publisher, out _);

        using var audio = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, audio, "audio/webm", 30);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == result.AnswerId);
        Assert.Equal(AnswerStatus.Scoring, saved.Status);
        Assert.NotNull(saved.LastScoringPublishedAt);

        // Session Ready -> InProgress
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.InProgress, s.Status);

        publisher.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_PublishThrows_AnswerStaysUploaded_NoMarker_NoException()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        t.Db.AddRange(session, q, crit);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("RabbitMQ down"));
        var svc = Build(t, publisher, out _);

        using var audio = new MemoryStream(new byte[] { 1 });
        // Publish hụt KHÔNG được làm hỏng upload.
        var result = await svc.UploadAnswerAsync(
            session.Id, q.Id, candidate, audio, "audio/webm", 30);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == result.AnswerId);
        Assert.Equal(AnswerStatus.Uploaded, saved.Status);
        Assert.Null(saved.LastScoringPublishedAt);  // tín hiệu publish-hụt cho republisher
    }

    [Fact]
    public async Task Upload_NoActiveRubric_SkipsPublish_StaysUploaded()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        // KHÔNG seed rubric criterion -> không có gì để chấm.
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        var svc = Build(t, publisher, out _);

        using var audio = new MemoryStream(new byte[] { 1 });
        var result = await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == result.AnswerId);
        Assert.Equal(AnswerStatus.Uploaded, saved.Status);
        Assert.Null(saved.LastScoringPublishedAt);
        publisher.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // E1: session B2B publish job mang ĐÚNG tiêu chí campaign (không phải rubric B2C cùng nghề).
    [Fact]
    public async Task Upload_B2BSession_PublishesCampaignCriteria_NotJobCategoryRubric()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready, campaignId: campaignId);
        var q = TestDb.Question(session.Id);
        // Tiêu chí campaign (đúng nguồn cần chấm) + rubric B2C cùng nghề (phải bị loại).
        var campaignCrit = TestDb.Criterion(session.JobCategory, campaignId: campaignId, name: "Campaign-Crit");
        var b2cCrit = TestDb.Criterion(session.JobCategory, name: "B2C-Crit");
        t.Db.AddRange(session, q, campaignCrit, b2cCrit);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        ScoringJob? published = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);
        var svc = Build(t, publisher, out _);

        using var audio = new MemoryStream(new byte[] { 1 });
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        Assert.NotNull(published);
        var crit = Assert.Single(published!.Criteria);
        Assert.Equal(campaignCrit.Id, crit.CriterionId);   // trỏ tiêu chí campaign
    }

    // E1: session B2C KHÔNG dính tiêu chí campaign cùng nghề (chống rò ngược).
    [Fact]
    public async Task Upload_B2CSession_PublishesJobCategoryRubric_NotCampaignCriteria()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);   // campaign_id = null
        var q = TestDb.Question(session.Id);
        var b2cCrit = TestDb.Criterion(session.JobCategory, name: "B2C-Crit");
        var campaignCrit = TestDb.Criterion(
            session.JobCategory, campaignId: Guid.NewGuid(), name: "Campaign-Crit");
        t.Db.AddRange(session, q, b2cCrit, campaignCrit);
        await t.Db.SaveChangesAsync();

        var publisher = new Mock<IScoringJobPublisher>();
        ScoringJob? published = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);
        var svc = Build(t, publisher, out _);

        using var audio = new MemoryStream(new byte[] { 1 });
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        Assert.NotNull(published);
        var crit = Assert.Single(published!.Criteria);
        Assert.Equal(b2cCrit.Id, crit.CriterionId);   // chỉ rubric B2C, không có campaign
    }

    // E1 Done-condition: session B2B Scored → answer_scores trỏ rubric_criteria(campaign_id).
    // E2: cùng lúc session đóng Scored, phải phát SessionScored kèm campaign_id + điểm.
    [Fact]
    public async Task B2BSession_WhenScored_AnswerScores_PointToCampaignCriteria()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

        // Notifier THẬT (tính điểm bằng data thật trong DB) — chỉ mock phần transport
        // (ISessionEventPublisher) để bắt message publish ra, đúng tinh thần "assert against
        // the publisher abstraction" thay vì cần RabbitMQ sống.
        var eventPublisher = new Mock<ISessionEventPublisher>();
        SessionScoredEvent? published = null;
        eventPublisher
            .Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SessionScoredEvent, CancellationToken>((e, _) => published = e)
            .Returns(Task.CompletedTask);
        var notifier = new SessionScoringNotifier(
            t.Db, eventPublisher.Object, TestDb.ResultService(t.Db),
            NullLogger<SessionScoringNotifier>.Instance);

        // Tạo session B2B qua đúng đường I1 (materialize tiêu chí campaign).
        var practice = new PracticeService(
            t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            notifier,
            new Mock<ICreditReservationClient>().Object,   // BC2: không dùng ở nhánh B2B
            new Mock<ISessionEventPublisher>().Object,     // BK12: không dùng ở nhánh B2B
            NullLogger<PracticeService>.Instance);
        var created = await practice.CreateCampaignSessionAsync(candidate,
            new CreateCampaignSessionRequest(
                campaignId, JobCategory.BE,
                Questions: new[] { "Q1" },
                Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) }));

        var campaignCrit = await t.Db.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CampaignId == campaignId);

        // Ứng viên nộp answer rồi submit (session -> Scoring).
        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");
        var publisher = new Mock<IScoringJobPublisher>();
        var svc = new AnswerService(
            t.Db, storage.Object, publisher.Object, notifier, NullLogger<AnswerService>.Instance);
        var questionId = created.Questions[0].Id;
        using var audio = new MemoryStream(new byte[] { 1 });
        var up = await svc.UploadAnswerAsync(created.Id, questionId, candidate, audio, "audio/webm", 30);
        await practice.SubmitSessionAsync(candidate, created.Id);

        // Worker callback chấm theo tiêu chí campaign (maxScore=5, weight=1.0 -> score=4 -> 80%).
        await svc.SaveResultAsync(up.AnswerId, new AnswerScoreCallbackRequest
        {
            Transcript = "trả lời",
            RubricVersion = campaignCrit.Version,
            Scores = { new ScoreItemDto { CriterionId = campaignCrit.Id, Score = 4m, Reasoning = "ok" } }
        });

        // Done: session Scored + mọi answer_scores trỏ rubric_criteria có campaign_id.
        var session = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == created.Id);
        Assert.Equal(SessionStatus.Scored, session.Status);

        var scoredCriterionIds = await t.Db.AnswerScores.AsNoTracking()
            .Where(asc => asc.Answer.SessionId == created.Id)
            .Select(asc => asc.CriterionId)
            .ToListAsync();
        Assert.NotEmpty(scoredCriterionIds);
        var campaignCriterionIds = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == campaignId)
            .Select(c => c.Id)
            .ToListAsync();
        Assert.All(scoredCriterionIds, id => Assert.Contains(id, campaignCriterionIds));

        // E2: SessionScored phát đúng 1 lần, mang campaign_id B2B + điểm tổng (4/5 = 80%).
        eventPublisher.Verify(p => p.PublishSessionScoredAsync(
            It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(published);
        Assert.Equal(created.Id, published!.SessionId);
        Assert.Equal(campaignId, published.CampaignId);
        Assert.Equal(candidate, published.CandidateId);
        Assert.Equal(80m, published.TotalScore);
    }

    [Fact]
    public async Task Upload_WrongCandidate_Throws()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var session = TestDb.Session(owner, SessionStatus.Ready);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        using var audio = new MemoryStream(new byte[] { 1 });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.UploadAnswerAsync(session.Id, q.Id, Guid.NewGuid(), audio, "audio/webm", 30));
    }

    [Fact]
    public async Task Upload_CompletedSession_Throws()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        using var audio = new MemoryStream(new byte[] { 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30));
    }

    [Fact]
    public async Task SaveResult_SavesScores_StatusScored()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);

        var req = new AnswerScoreCallbackRequest
        {
            Transcript = "Đây là câu trả lời",
            RubricVersion = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 4.5m, Reasoning = "ok" } }
        };
        await svc.SaveResultAsync(answer.Id, req);

        var saved = await t.Db.PracticeAnswers.AsNoTracking().Include(a => a.Scores)
            .FirstAsync(a => a.Id == answer.Id);
        Assert.Equal(AnswerStatus.Scored, saved.Status);
        Assert.Equal("Đây là câu trả lời", saved.Transcript);
        Assert.Single(saved.Scores);
        Assert.Equal(4.5m, saved.Scores.First().Score);

        // Answer cuối Scored -> session đóng sang Scored.
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);
    }

    // E2: session B2C (campaign_id = null) đóng Scored VẪN phải phát SessionScored
    // (campaign_id null trong message là hợp lệ — Payment vẫn cần biết để consume credit cá nhân).
    [Fact]
    public async Task SaveResult_B2CSession_WhenScored_PublishesSessionScoredEvent_WithNullCampaignId()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);   // campaign_id = null
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _, out var notifier);

        var req = new AnswerScoreCallbackRequest
        {
            Transcript = "Đây là câu trả lời",
            RubricVersion = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 4.5m, Reasoning = "ok" } }
        };
        await svc.SaveResultAsync(answer.Id, req);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);

        // Event phải phát đúng 1 lần cho ĐÚNG session vừa đóng, dù campaign_id = null (B2C).
        notifier.Verify(n => n.NotifySessionScoredAsync(session.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // E2: notifier THẬT (không mock) — kiểm message SessionScored đúng shape khi B2C Scored:
    // campaign_id null, candidate_id khớp session, điểm tổng tính đúng (1 tiêu chí, maxScore 5,
    // score 4 -> 80%).
    [Fact]
    public async Task SaveResult_B2CSession_WhenScored_EventCarriesCandidateId_AndComputedScore()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);   // campaign_id = null
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);   // maxScore=5, weight=1.0
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        var eventPublisher = new Mock<ISessionEventPublisher>();
        SessionScoredEvent? published = null;
        eventPublisher
            .Setup(p => p.PublishSessionScoredAsync(It.IsAny<SessionScoredEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SessionScoredEvent, CancellationToken>((e, _) => published = e)
            .Returns(Task.CompletedTask);
        var notifier = new SessionScoringNotifier(
            t.Db, eventPublisher.Object, TestDb.ResultService(t.Db),
            NullLogger<SessionScoringNotifier>.Instance);

        var storage = new Mock<IStorageService>();
        var svc = new AnswerService(
            t.Db, storage.Object, new Mock<IScoringJobPublisher>().Object, notifier,
            NullLogger<AnswerService>.Instance);

        await svc.SaveResultAsync(answer.Id, new AnswerScoreCallbackRequest
        {
            Transcript = "trả lời",
            RubricVersion = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 4m, Reasoning = "ok" } }
        });

        Assert.NotNull(published);
        Assert.Equal(session.Id, published!.SessionId);
        Assert.Null(published.CampaignId);            // B2C
        Assert.Equal(candidate, published.CandidateId);
        Assert.Equal(80m, published.TotalScore);       // 4/5 * 100 * weight(1.0) / Σweight(1.0)
    }

    [Fact]
    public async Task SaveResult_CalledTwice_IsIdempotent_NoDuplicateScores()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        var req = new AnswerScoreCallbackRequest
        {
            Transcript = "x",
            RubricVersion = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 3m, Reasoning = "a" } }
        };

        await svc.SaveResultAsync(answer.Id, req);
        await svc.SaveResultAsync(answer.Id, req);   // worker retry gửi lại

        var count = await t.Db.AnswerScores.AsNoTracking().CountAsync(s => s.AnswerId == answer.Id);
        Assert.Equal(1, count);   // không nhân đôi
    }

    // E8 (a): worker/image lệch trả điểm vượt trần -> C# KẸP về maxScore (INT-9). B2C.
    [Fact]
    public async Task SaveResult_ScoreAboveMaxScore_IsClampedToMaxScore()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);   // B2C
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);   // maxScore = 5
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        await svc.SaveResultAsync(answer.Id, new AnswerScoreCallbackRequest
        {
            Transcript = "x",
            RubricVersion = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 99m, Reasoning = "worker lệch" } }
        });

        var saved = await t.Db.AnswerScores.AsNoTracking().SingleAsync(s => s.AnswerId == answer.Id);
        Assert.Equal(5m, saved.Score);   // kẹp về maxScore, không lưu 99
    }

    // E8 (b): criterionId AI bịa (không thuộc rubric session) -> BỎ; tiêu chí hợp lệ vẫn lưu bình thường. B2C.
    [Fact]
    public async Task SaveResult_CriterionNotInRubric_IsDropped_ValidKept()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);   // B2C
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);   // rubric hợp lệ, maxScore 5
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        await svc.SaveResultAsync(answer.Id, new AnswerScoreCallbackRequest
        {
            Transcript = "x",
            RubricVersion = 1,
            Scores =
            {
                new ScoreItemDto { CriterionId = crit.Id, Score = 3m, Reasoning = "hợp lệ" },
                new ScoreItemDto { CriterionId = Guid.NewGuid(), Score = 4m, Reasoning = "AI bịa" }
            }
        });

        var saved = await t.Db.AnswerScores.AsNoTracking().Where(s => s.AnswerId == answer.Id).ToListAsync();
        Assert.Single(saved);                        // chỉ tiêu chí hợp lệ được lưu (criterion lạ bị bỏ)
        Assert.Equal(crit.Id, saved[0].CriterionId);
        Assert.Equal(3m, saved[0].Score);            // trường hợp hợp lệ lưu nguyên điểm
    }

    // E8: guard áp cho CẢ B2B — criterion B2C cùng nghề (campaign_id null, ngoài rubric campaign) bị BỎ,
    // đồng thời điểm vượt trần của tiêu chí campaign bị KẸP.
    [Fact]
    public async Task SaveResult_B2BSession_DropsCriterionOutsideCampaignRubric_AndClamps()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring, campaignId: campaignId);   // B2B
        var q = TestDb.Question(session.Id);
        var campaignCrit = TestDb.Criterion(session.JobCategory, campaignId: campaignId, name: "Campaign-Crit"); // maxScore 5
        var b2cCrit = TestDb.Criterion(session.JobCategory, name: "B2C-Crit");   // campaign_id null -> NGOÀI rubric B2B
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, campaignCrit, b2cCrit, answer);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        await svc.SaveResultAsync(answer.Id, new AnswerScoreCallbackRequest
        {
            Transcript = "x",
            RubricVersion = campaignCrit.Version,
            Scores =
            {
                new ScoreItemDto { CriterionId = campaignCrit.Id, Score = 9m },   // vượt trần -> kẹp về 5
                new ScoreItemDto { CriterionId = b2cCrit.Id, Score = 4m }         // ngoài rubric B2B -> bỏ
            }
        });

        var saved = await t.Db.AnswerScores.AsNoTracking().Where(s => s.AnswerId == answer.Id).ToListAsync();
        Assert.Single(saved);
        Assert.Equal(campaignCrit.Id, saved[0].CriterionId);
        Assert.Equal(5m, saved[0].Score);   // kẹp về maxScore
    }

    [Fact]
    public async Task MarkFailed_SetsFailed_AndClosesSession_WhenScoring()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        // Answer đang Scoring là answer cuối -> mark Failed phải đóng session sang Scored.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        await svc.MarkFailedAsync(a.Id, "audio hỏng");

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id);
        Assert.Equal(AnswerStatus.Failed, saved.Status);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);   // Failed tính là "xong" -> đóng được
    }

    [Fact]
    public async Task MarkFailed_AlreadyScored_IsNoOp()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        await svc.MarkFailedAsync(a.Id, "callback đến muộn");

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == a.Id);
        Assert.Equal(AnswerStatus.Scored, saved.Status);   // KHÔNG hạ Scored xuống Failed
    }

    [Fact]
    public async Task MarkFailed_UnknownAnswer_Throws()
    {
        using var t = new TestDb();
        var svc = Build(t, new Mock<IScoringJobPublisher>(), out _);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.MarkFailedAsync(Guid.NewGuid(), "x"));
    }
}
