using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// F12 (FR03) — 2 tiêu chí NGÔN NGỮ ("Ngữ pháp &amp; dùng từ" · "Thuật ngữ chuyên ngành") trong
/// rubric B2C seed của cả 3 nghề.
///
/// <para><b>Rủi ro chính task này canh</b> (INT-9): thêm tiêu chí vào rubric mà đường publish và
/// đường callback KHÔNG chọn cùng bộ ⇒ AI chấm thiếu tiêu chí ⇒ answer <c>Failed</c> hàng loạt.
/// Nên các test dưới đi TRỌN chuỗi publish → callback → breakdown, không chỉ kiểm nội dung seed.</para>
///
/// <para><b>BC16</b>: candidate đã có rubric RIÊNG thì KHÔNG tự nhận tiêu chí seed mới —
/// <see cref="B2CRubricScope.ResolveOwnerAsync"/> vẫn ưu tiên rubric riêng.</para>
///
/// Seed <c>HasData</c> chỉ áp Npgsql → test SQLite nạp tay <see cref="B2CRubricSeed.Build"/>
/// (mẫu <see cref="B2CRubricSeedTests"/>).
/// </summary>
public class LanguageRubricCriteriaTests
{
    private static readonly JobCategory[] AllCategories =
        [JobCategory.BA, JobCategory.BE, JobCategory.FE];

    private static async Task ApplySeedAsync(InterviewDbContext db)
    {
        db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await db.SaveChangesAsync();
    }

    // (1) Cả 3 nghề đều có ĐỦ 2 tiêu chí ngôn ngữ, active, thuộc rubric B2C (campaign_id NULL).
    [Fact]
    public async Task Seed_EveryJobCategory_HasLanguageAndTerminologyCriteria()
    {
        using var t = new TestDb();
        await ApplySeedAsync(t.Db);

        foreach (var cat in AllCategories)
        {
            var rows = await t.Db.RubricCriteria.AsNoTracking()
                .Where(c => c.CampaignId == null && c.CandidateId == null
                            && c.IsActive && c.JobCategory == cat)
                .ToListAsync();

            Assert.Contains(rows, c => c.Name == B2CRubricSeed.LanguageName);
            Assert.Contains(rows, c => c.Name == B2CRubricSeed.TerminologyName);
            Assert.Equal(1.0m, rows.Sum(c => c.Weight));   // rebalance vẫn giữ Σ=1 (INT-10)
        }
    }

    // (2) Tiêu chí "Thuật ngữ" phải NEO theo nghề — mô tả nêu ví dụ thuật ngữ riêng của nghề đó,
    //     nếu không thì AI không có gì để phân biệt "sai thuật ngữ BE" với "sai thuật ngữ FE".
    [Fact]
    public void TerminologyCriterion_DescriptionIsRoleSpecific()
    {
        var seed = B2CRubricSeed.Build();

        string Desc(JobCategory cat) => seed
            .Single(c => c.JobCategory == cat && c.Name == B2CRubricSeed.TerminologyName)
            .Description!;

        Assert.Contains("user story", Desc(JobCategory.BA));
        Assert.Contains("idempotent", Desc(JobCategory.BE));
        Assert.Contains("hydration", Desc(JobCategory.FE));

        // 3 nghề dùng 3 mô tả KHÁC nhau (không copy-paste chung một câu chung chung).
        Assert.Equal(3, AllCategories.Select(Desc).Distinct().Count());
    }

    // (3) Tiêu chí ngữ pháp KHÔNG được chấm chính tả/dấu câu — transcript là ASR (Whisper),
    //     lỗi đó là của bộ nhận dạng chứ không phải của ứng viên.
    [Fact]
    public void LanguageCriterion_ExcludesAsrArtifacts()
    {
        var desc = B2CRubricSeed.Build()
            .First(c => c.Name == B2CRubricSeed.LanguageName)
            .Description!;

        Assert.Contains("KHÔNG xét chính tả", desc);
    }

    // (4) 🔑 INT-9 — publish job chấm cho B2C PHẢI mang cả 2 tiêu chí mới. Thiếu ⇒ AI không được
    //     yêu cầu chấm chúng ⇒ gemini.score() báo "chấm thiếu tiêu chí" ⇒ answer Failed.
    [Theory]
    [InlineData(JobCategory.BA)]
    [InlineData(JobCategory.BE)]
    [InlineData(JobCategory.FE)]
    public async Task B2CUpload_PublishesJob_IncludingLanguageCriteria(JobCategory cat)
    {
        using var t = new TestDb();
        await ApplySeedAsync(t.Db);

        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready, cat: cat);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var (svc, publisher) = BuildAnswerService(t);
        ScoringJob? published = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);

        using var audio = new MemoryStream([1, 2, 3]);
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        Assert.NotNull(published);
        var names = published!.Criteria.Select(c => c.Name).ToList();
        Assert.Contains(B2CRubricSeed.LanguageName, names);
        Assert.Contains(B2CRubricSeed.TerminologyName, names);
        Assert.Equal(6, published.Criteria.Count);   // 4 cũ + 2 mới
    }

    // (5) 🔑 INT-9 — callback chấm ĐỦ 6 tiêu chí (đúng bộ đã publish) → answer Scored, KHÔNG Failed,
    //     và điểm tiêu chí ngôn ngữ được LƯU (không bị E8 drop vì "ngoài rubric").
    [Fact]
    public async Task Callback_ScoringAllSixCriteria_SavesLanguageScores_AndAnswerScored()
    {
        using var t = new TestDb();
        await ApplySeedAsync(t.Db);

        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress, cat: JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, answer);
        await t.Db.SaveChangesAsync();

        var criteria = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == JobCategory.BE)
            .ToListAsync();

        // Ứng viên dùng SAI thuật ngữ → tiêu chí "Thuật ngữ chuyên ngành" điểm thấp (1/5),
        // các tiêu chí khác vẫn khá (4/5). Đây là hành vi FR03 mô tả.
        var scores = criteria.Select(c => new ScoreItemDto
        {
            CriterionId = c.Id,
            Score = c.Name == B2CRubricSeed.TerminologyName ? 1m : 4m,
            LevelMatched = c.Name == B2CRubricSeed.TerminologyName ? 1 : 4,
            Reasoning = "Ứng viên nói \"cái transaction đó nó khoá bảng lại\" — dùng sai khái niệm."
        }).ToList();

        var (svc, _) = BuildAnswerService(t);
        await svc.SaveResultAsync(answer.Id, new AnswerScoreCallbackRequest
        {
            Transcript = "cái transaction đó nó khoá bảng lại",
            RubricVersion = B2CRubricSeed.RubricVersion,
            AttemptNo = 1,
            Scores = scores
        });

        var saved = await t.Db.PracticeAnswers.AsNoTracking()
            .Include(a => a.Scores)
            .FirstAsync(a => a.Id == answer.Id);

        Assert.Equal(AnswerStatus.Scored, saved.Status);          // KHÔNG Failed vì thiếu tiêu chí
        Assert.Equal(6, saved.Scores.Count);                      // đủ 6, không bị E8 drop cái nào

        var terminologyId = criteria.Single(c => c.Name == B2CRubricSeed.TerminologyName).Id;
        var termScore = saved.Scores.Single(s => s.CriterionId == terminologyId);
        Assert.Equal(1m, termScore.Score);                        // sai thuật ngữ → điểm thấp
    }

    // (6) 🔑 BC16 — candidate CÓ rubric riêng thì KHÔNG bị trộn tiêu chí seed mới vào: resolver
    //     vẫn trả owner = candidate, và job publish chỉ mang rubric riêng của họ.
    [Fact]
    public async Task CandidateWithCustomRubric_DoesNotInheritNewSeedCriteria()
    {
        using var t = new TestDb();
        await ApplySeedAsync(t.Db);

        var candidate = Guid.NewGuid();
        var own = TestDb.Criterion(JobCategory.BE, name: "Tiêu chí của tôi", candidateId: candidate);
        t.Db.RubricCriteria.Add(own);
        await t.Db.SaveChangesAsync();

        // Resolver vẫn ưu tiên rubric riêng dù seed vừa có thêm tiêu chí.
        var owner = await B2CRubricScope.ResolveOwnerAsync(t.Db, candidate, JobCategory.BE);
        Assert.Equal(candidate, owner);

        var session = TestDb.Session(candidate, SessionStatus.Ready, cat: JobCategory.BE);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        await t.Db.SaveChangesAsync();

        var (svc, publisher) = BuildAnswerService(t);
        ScoringJob? published = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);

        using var audio = new MemoryStream([1, 2, 3]);
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        Assert.NotNull(published);
        Assert.Equal(["Tiêu chí của tôi"], published!.Criteria.Select(c => c.Name));
        Assert.DoesNotContain(B2CRubricSeed.LanguageName, published.Criteria.Select(c => c.Name));
    }

    // (7) B2B (campaign_id != null) KHÔNG dùng rubric B2C ⇒ tiêu chí seed mới không rò sang campaign
    //     (E1 fairness: ranking chỉ so theo tiêu chí campaign HR khai).
    [Fact]
    public async Task B2BSession_DoesNotReceiveB2CLanguageCriteria()
    {
        using var t = new TestDb();
        await ApplySeedAsync(t.Db);

        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var campaignCrit = TestDb.Criterion(JobCategory.BE, name: "Tiêu chí campaign", campaignId: campaignId);
        var session = TestDb.Session(candidate, SessionStatus.InProgress, cat: JobCategory.BE, campaignId: campaignId);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(campaignCrit, session, q);
        await t.Db.SaveChangesAsync();

        var (svc, publisher) = BuildAnswerService(t);
        ScoringJob? published = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);

        using var audio = new MemoryStream([1, 2, 3]);
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        Assert.NotNull(published);
        Assert.Equal(["Tiêu chí campaign"], published!.Criteria.Select(c => c.Name));
    }

    private static (AnswerService Svc, Mock<IScoringJobPublisher> Publisher) BuildAnswerService(TestDb t)
    {
        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/f12.webm");

        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var publisher = new Mock<IScoringJobPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new AnswerService(
            t.Db, storage.Object, publisher.Object, notifier.Object,
            TestDb.ScoringOpts(), NullLogger<AnswerService>.Instance);

        return (svc, publisher);
    }
}
