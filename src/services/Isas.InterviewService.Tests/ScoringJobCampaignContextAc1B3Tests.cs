using System.Reflection;
using System.Text.Json;
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
/// AC1-B3 — <c>ScoringJob</c> phải mang ngữ cảnh chiến dịch (<c>CampaignId</c> + <c>CandidateId</c>)
/// để worker AIService gửi cờ chống gian lận về ĐÚNG buổi thi (B5 sẽ tiêu thụ).
///
/// <para>Hai bất biến được khoá ở đây:</para>
/// <list type="number">
/// <item>Van B2B: chỉ buổi thuộc campaign mới mang cặp này. B2C = null (BC-6 — luyện tập KHÔNG có
/// anti-cheat). Session nào cũng có <c>CandidateId</c> nên vế null <b>chỉ tới được nhờ van</b>,
/// không phải "tình cờ chưa ai set".</item>
/// <item>CẢ HAI đường publish đều điền: <see cref="AnswerService"/> (lúc upload) và
/// <see cref="StuckAnswerRepublisher"/> (đường cứu hộ). Bỏ sót một đường ⇒ buổi nào phải cứu bằng
/// republisher mất ngữ cảnh IM LẶNG, không lỗi nào nổ — đúng lớp lỗi mà F11, đáp án mẫu và rubric
/// ghim đều đã dính ở CHÍNH cặp đường này.</item>
/// </list>
/// </summary>
public class ScoringJobCampaignContextAc1B3Tests
{
    // ═════════════════ (1) Đường publish lúc upload — AnswerService ═════════════════

    private static async Task<ScoringJob> UploadAndCapture(
        TestDb t, PracticeSession session, PracticeQuestion question)
    {
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/x.webm");

        var pub = new Mock<IScoringJobPublisher>();
        ScoringJob? captured = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);

        var svc = new AnswerService(
            t.Db, storage.Object, pub.Object, new Mock<ISessionScoringNotifier>().Object,
            Options.Create(new ScoringOptions()), NullLogger<AnswerService>.Instance);

        await svc.UploadAnswerAsync(
            session.Id, question.Id, session.CandidateId,
            new MemoryStream([1, 2, 3]), "audio/webm", 30);

        Assert.NotNull(captured);
        return captured!;
    }

    [Fact]
    public async Task Upload_B2BSession_JobMangCampaignIdVaCandidateId()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var session = TestDb.Session(
            candidateId, SessionStatus.Ready, JobCategory.BE, campaignId: campaignId);
        var question = TestDb.Question(session.Id);
        t.Db.AddRange(session, question, TestDb.Criterion(session.JobCategory, campaignId: campaignId));
        await t.Db.SaveChangesAsync();

        var job = await UploadAndCapture(t, session, question);

        Assert.Equal(campaignId, job.CampaignId);
        Assert.Equal(candidateId, job.CandidateId);
    }

    // 🔒 Van B2C: session LUÔN có CandidateId (cột non-nullable), nên hai vế null dưới đây chỉ đúng
    // được nếu van thật sự tồn tại — không có ca "null vì chưa ai set".
    [Fact]
    public async Task Upload_B2CSession_CaHaiFieldLaNull_DuSessionCoCandidateId()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var session = TestDb.Session(candidateId, SessionStatus.Ready, JobCategory.BE);
        var question = TestDb.Question(session.Id);
        t.Db.AddRange(session, question, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        Assert.NotEqual(Guid.Empty, session.CandidateId);   // tiền đề: session CÓ candidate

        var job = await UploadAndCapture(t, session, question);

        Assert.Null(job.CampaignId);
        Assert.Null(job.CandidateId);
    }

    // ═════════════════ (2) Đường cứu hộ — StuckAnswerRepublisher ═════════════════

    private static async Task ScanOnce(StuckAnswerRepublisher r)
    {
        var mi = typeof(StuckAnswerRepublisher)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    private static async Task<ScoringJob> RepublishAndCapture(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(
            o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var pub = new Mock<IScoringJobPublisher>();
        ScoringJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => published = j)
            .Returns(Task.CompletedTask);

        var r = new StuckAnswerRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            Options.Create(new RepublisherSettings { BatchSize = 200 }),
            Options.Create(new ScoringOptions()),
            NullLogger<StuckAnswerRepublisher>.Instance);

        await ScanOnce(r);

        Assert.NotNull(published);
        return published!;
    }

    // 🔒 PHÉP QUAN TRỌNG NHẤT của B3. Đường cứu hộ dựng job từ một anonymous projection tách hẳn
    // khỏi entity `PracticeSession` — điền ở AnswerService rồi quên ở đây là hỏng câm: answer chấm
    // trơn tru có ngữ cảnh, answer phải cứu thì không, mà cả hai cùng một chiến dịch.
    [Fact]
    public async Task Republish_B2BSession_JobCungMangCampaignIdVaCandidateId()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var session = TestDb.Session(
            candidateId, SessionStatus.InProgress, JobCategory.BE, campaignId: campaignId);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        t.Db.AddRange(session, q, a, TestDb.Criterion(session.JobCategory, campaignId: campaignId));
        await t.Db.SaveChangesAsync();

        var job = await RepublishAndCapture(t);

        Assert.Equal(campaignId, job.CampaignId);
        Assert.Equal(candidateId, job.CandidateId);
    }

    [Fact]
    public async Task Republish_B2CSession_CaHaiFieldLaNull_DuSessionCoCandidateId()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var session = TestDb.Session(candidateId, SessionStatus.InProgress, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        t.Db.AddRange(session, q, a, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        Assert.NotEqual(Guid.Empty, session.CandidateId);   // tiền đề: session CÓ candidate

        var job = await RepublishAndCapture(t);

        Assert.Null(job.CampaignId);
        Assert.Null(job.CandidateId);
    }

    // ═════════════════ (3) Hợp đồng dây RabbitMQ ═════════════════

    // Serialize y hệt `ScoringJobPublisher`: `JsonSerializer.Serialize(job)` KHÔNG kèm options
    // ⇒ JsonSerializerOptions.Default ⇒ khoá trên hàng đợi là PascalCase.
    //
    // Chấp nhận CẢ HAI casing (mẫu `TranscriptEngineWireContractTests`): worker Python đọc
    // `body.get("x") or body.get("X")` nên đổi convention serialize KHÔNG phá production ⇒ không đỏ
    // oan. Nhưng XOÁ field hay đổi sang `campaign_id` thì rơi khỏi tập này ⇒ ĐỎ, đúng thứ cần bắt.
    [Fact]
    public void ScoringJob_MangNguCanhCampaign_TheoDungCachPublisherSerialize()
    {
        var campaignId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var job = new ScoringJob
        {
            AnswerId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            QuestionId = Guid.NewGuid(),
            AudioObjectKey = "answer-audio/x.webm",
            QuestionContent = "q",
            JobCategory = "BE",
            RubricVersion = 1,
            CampaignId = campaignId,
            CandidateId = candidateId
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(job));

        AssertWireKey(doc, "CampaignId", "campaignId", campaignId);
        AssertWireKey(doc, "CandidateId", "candidateId", candidateId);

        // SessionId đã có từ trước — khoá lại ở đây vì cả ba đi cùng chuyến mới định danh được
        // buổi thi ở phía nhận.
        Assert.Contains(doc.RootElement.EnumerateObject().Select(p => p.Name),
            n => n is "SessionId" or "sessionId");
    }

    private static void AssertWireKey(JsonDocument doc, string pascal, string camel, Guid expected)
    {
        var key = doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .FirstOrDefault(n => n == pascal || n == camel);

        Assert.True(key is not null,
            $"ScoringJob không mang '{pascal}' (hoặc '{camel}') trên dây RabbitMQ. Worker AIService "
            + "cần cặp (campaign, candidate) để gửi cờ chống gian lận về đúng buổi thi — thiếu khoá "
            + "này là cờ không bao giờ tới được HR, và không lỗi nào nổ.");
        Assert.Equal(expected, doc.RootElement.GetProperty(key!).GetGuid());
    }
}
