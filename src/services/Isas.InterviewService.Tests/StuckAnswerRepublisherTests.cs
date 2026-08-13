using System.Data.Common;
using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    // DB29: interceptor tuỳ chọn để ĐẾM query thật (chứng minh rubric lookup được hoist khỏi vòng lặp).
    private static (StuckAnswerRepublisher r, Mock<IScoringJobPublisher> pub) Build(
        TestDb t, int batchSize = 200, IInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o =>
        {
            o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention();
            if (interceptor is not null) o.AddInterceptors(interceptor);
        });
        var provider = services.BuildServiceProvider();

        var pub = new Mock<IScoringJobPublisher>();
        var r = new StuckAnswerRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            Options.Create(new RepublisherSettings { BatchSize = batchSize }),
            Options.Create(new ScoringOptions()),
            NullLogger<StuckAnswerRepublisher>.Instance);
        return (r, pub);
    }

    // Đếm SELECT chạm bảng rubric_criteria (nạp tiêu chí + resolve owner BC16).
    private sealed class RubricQueryCounter : DbCommandInterceptor
    {
        public int Count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("rubric_criteria", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref Count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ReaderExecuting(command, eventData, result));
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

    // Adaptive: answer đã có transcript đồng bộ → re-publish job mang theo transcript (worker bỏ Whisper).
    [Fact]
    public async Task PublishHut_WithSyncTranscript_CarriesTranscriptInJob()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        a.Transcript = "transcript đồng bộ đã có";   // adaptive: đã transcribe khi decide-next
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t);
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
           .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        Assert.Equal("transcript đồng bộ đã có", published!.Transcript);
    }

    // F11 — re-publish phải mang theo CẢ chỉ số cách nói, không chỉ transcript.
    //
    // Vì sao cần test riêng: republisher KHÔNG gọi lại AIService, và worker BỎ Whisper khi job đã
    // có transcript ⇒ chỉ số không đi kèm ở đây thì đúng những answer phải cứu bằng republisher
    // (broker trục trặc / worker chết — tức là lúc đã có sự cố) sẽ mất chỉ số, còn answer chấm trơn
    // tru vẫn có. Lệch âm thầm, không lỗi nào nổ.
    //
    // ⚠ Lỗ này tìm ra BẰNG mutation-check: đặt `DeliveryMetrics = null` trong republisher mà cả
    // 413 test vẫn XANH — bộ test lúc đó chỉ phủ mapper, không phủ chỗ đấu dây này.
    [Fact]
    public async Task PublishHut_WithDeliveryMetrics_CarriesMetricsInJob()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        a.Transcript = "ừm transcript đồng bộ";
        DeliveryMetricsMapper.Apply(a, new DeliveryMetricsDto
        {
            SpeechRateWpm = 190,
            FillerCount = 6,
            PauseCount = 4,
            LongestPauseSec = 3.1,
            SilenceRatio = 0.4,
            // Vá 2026-07-19 — 4 chỉ số THỜI GIAN/mật độ. Chúng là thứ prompt chấm dặn LLM tin
            // NHẤT, nên đường cứu mà đánh rơi chúng thì answer được republish bị chấm trôi chảy
            // bằng "0 giây audio".
            AudioSec = 70.0,
            SpeechSec = 55.5,
            WordCount = 160,
            FillerPer100Words = 3.75,
            FillerBreakdown = new Dictionary<string, int> { ["ừm"] = 6 },
            MetricsVersion = 2,
        });
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t);
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
           .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        Assert.NotNull(published!.DeliveryMetrics);
        Assert.Equal(190, published.DeliveryMetrics!.SpeechRateWpm);
        Assert.Equal(6, published.DeliveryMetrics.FillerCount);
        Assert.Equal(6, published.DeliveryMetrics.FillerBreakdown["ừm"]);

        // ⚠ Vá 2026-07-19 — 4 assert dưới đây tồn tại vì overload `Read()` cho 4 tham số mới
        // giá trị mặc định `null` (để call site cũ khỏi sửa). Tiện cho người gọi, nhưng nghĩa là
        // BỎ QUÊN chúng ở đây sẽ biên dịch sạch và im lặng — đúng cách lỗi gốc đã lọt.
        Assert.Equal(70.0, published.DeliveryMetrics.AudioSec);
        Assert.Equal(55.5, published.DeliveryMetrics.SpeechSec);
        Assert.Equal(160, published.DeliveryMetrics.WordCount);
        Assert.Equal(3.75, published.DeliveryMetrics.FillerPer100Words);

        // Con dấu thước đo (2026-08-05) rơi vào ĐÚNG cái bẫy mô tả ngay trên: nó cũng là tham số
        // có default `null` ở overload `Read()`. Bỏ quên ở projection của republisher thì answer
        // đi đường cứu mất dấu, trong khi answer chấm trơn tru vẫn có — hai bộ số cùng một buổi
        // lại mang hai lai lịch khác nhau.
        Assert.Equal(2, published.DeliveryMetrics.MetricsVersion);
    }

    // Mặt còn lại: answer CHƯA từng đo → job mang null (KHÔNG phải DTO toàn 0). Gửi 0 sẽ khiến
    // worker tưởng "đã đo, kết quả 0" rồi bỏ qua việc đo thật ⇒ chỉ số bịa cho mọi answer tĩnh.
    [Fact]
    public async Task PublishHut_ChuaDoChiSo_JobMangNull()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t);
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
           .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.NotNull(published);
        Assert.Null(published!.DeliveryMetrics);
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

    // ── DB29 ──────────────────────────────────────────────────────────────

    // Tồn đọng lớn hơn trần batch → mỗi vòng chỉ tiêu hoá tối đa BatchSize (không nạp hết vào RAM).
    // Gỡ .Take(...) khỏi production → publish 5 lần → ĐỎ.
    [Fact]
    public async Task Db29_BatchSize_CapsRowsProcessedPerScan()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        t.Db.AddRange(session, q);
        for (var i = 0; i < 5; i++)
        {
            var q2 = TestDb.Question(session.Id, order: i + 2);
            t.Db.Add(q2);
            t.Db.Add(TestDb.Answer(session.Id, q2.Id, AnswerStatus.Uploaded,
                DateTime.UtcNow.AddMinutes(-10 - i), lastPublished: null));
        }
        await t.Db.SaveChangesAsync();
        await SeedActiveCriterion(t, session.JobCategory);

        var (r, pub) = Build(t, batchSize: 2);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // N answer cùng 1 campaign ⇒ CÙNG bộ tiêu chí ⇒ chỉ được tra rubric_criteria 1 lần (hoist khỏi vòng lặp).
    // Đưa lookup trở lại trong foreach → 4 query → ĐỎ.
    [Fact]
    public async Task Db29_SameCampaign_LoadsCriteriaOnce_NotPerAnswer()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        t.Db.Add(TestDb.Criterion(JobCategory.BE, campaignId: campaignId, name: "Campaign-Crit"));
        for (var i = 0; i < 4; i++)
        {
            // 4 ứng viên KHÁC NHAU trong cùng campaign — đúng hình dạng tải thật của B2B.
            var s = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress, campaignId: campaignId);
            var q = TestDb.Question(s.Id);
            t.Db.AddRange(s, q, TestDb.Answer(s.Id, q.Id, AnswerStatus.Uploaded,
                DateTime.UtcNow.AddMinutes(-10), lastPublished: null));
        }
        await t.Db.SaveChangesAsync();

        var counter = new RubricQueryCounter();
        var (r, pub) = Build(t, interceptor: counter);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
        Assert.Equal(1, counter.Count);
    }

    // Hoist KHÔNG được trộn scope: 2 candidate B2C khác nhau (một có rubric riêng BC16, một dùng seed)
    // phải nhận đúng tiêu chí của mình. Cache theo answer-id/khoá quá rộng sẽ làm sai chỗ này.
    [Fact]
    public async Task Db29_TwoB2CCandidates_EachGetsOwnRubric()
    {
        using var t = new TestDb();
        var withCustom = Guid.NewGuid();
        var withSeed = Guid.NewGuid();
        var seed = TestDb.Criterion(JobCategory.BE, name: "Seed-Crit");
        var custom = TestDb.Criterion(JobCategory.BE, name: "Custom-Crit", candidateId: withCustom);
        t.Db.AddRange(seed, custom);

        var sessions = new Dictionary<Guid, Guid>();   // candidateId -> sessionId
        foreach (var cand in new[] { withCustom, withSeed })
        {
            var s = TestDb.Session(cand, SessionStatus.InProgress);
            var q = TestDb.Question(s.Id);
            t.Db.AddRange(s, q, TestDb.Answer(s.Id, q.Id, AnswerStatus.Uploaded,
                DateTime.UtcNow.AddMinutes(-10), lastPublished: null));
            sessions[cand] = s.Id;
        }
        await t.Db.SaveChangesAsync();

        var (r, pub) = Build(t);
        var published = new List<ScoringJob>();
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
           .Callback<ScoringJob, CancellationToken>((j, _) => published.Add(j))
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        Assert.Equal(2, published.Count);
        var customJob = published.Single(j => j.SessionId == sessions[withCustom]);
        var seedJob = published.Single(j => j.SessionId == sessions[withSeed]);
        Assert.Equal(custom.Id, Assert.Single(customJob.Criteria).CriterionId);
        Assert.Equal(seed.Id, Assert.Single(seedJob.Criteria).CriterionId);
    }
}
