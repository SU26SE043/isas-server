using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// I1: tạo session B2B (campaign_id) + materialize tiêu chí campaign → rubric_criteria(campaign_id).
/// Câu hỏi + tiêu chí do Campaign cấp; không gọi AI (storage/generator mock, không dùng).
/// </summary>
public class CampaignSessionTests
{
    private static PracticeService Build(TestDb t, int maxConcurrentSessions = 0, Mock<ICreditReservationClient>? credits = null)
    {
        var scoringNotifier = new Mock<ISessionScoringNotifier>();
        scoringNotifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // BK14: B2B reserve ví ORG khi tạo session → mock trả reservation hợp lệ cho mọi ownerType.
        var reservation = credits ?? new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(t.Db, new Mock<IStorageService>().Object,
               new Mock<IAiServiceQuestionGenerator>().Object,
               scoringNotifier.Object,
               reservation.Object,
               NullLogger<PracticeService>.Instance,
               capacityOptions: Options.Create(new CapacityOptions { MaxConcurrentSessions = maxConcurrentSessions }));
    }

    [Fact]
    public async Task CreateCampaignSession_PersistsCampaignId_AndMaterializesCriteria()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var svc = Build(t);

        var req = new CreateCampaignSessionRequest(
            campaignId, Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1", "Q2" },
            Criteria: new[]
            {
                new CampaignCriterionInput("Communication", null, 0.4m, 5),
                new CampaignCriterionInput("Technical depth", "chiều sâu kỹ thuật", 0.6m, 5)
            });

        var res = await svc.CreateCampaignSessionAsync(candidate, req);

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        Assert.Equal(2, res.Questions.Count);

        await using var read = t.NewContext();
        var session = await read.PracticeSessions.AsNoTracking().SingleAsync(s => s.Id == res.Id);
        Assert.Equal(campaignId, session.CampaignId);

        var criteria = await read.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == campaignId).ToListAsync();
        Assert.Equal(2, criteria.Count);
        Assert.All(criteria, c => Assert.True(c.IsActive));
        Assert.Contains(criteria, c => c.Name == "Communication" && c.Weight == 0.4m && c.MaxScore == 5);
        Assert.Contains(criteria, c => c.Name == "Technical depth" && c.Description == "chiều sâu kỹ thuật");
    }

    // rubric_criteria keyed by campaign_id → session thứ 2 cùng campaign KHÔNG nhân đôi tiêu chí.
    [Fact]
    public async Task CreateCampaignSession_SecondSessionSameCampaign_DoesNotDuplicateCriteria()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var svc = Build(t);
        var req = new CreateCampaignSessionRequest(
            campaignId, Guid.NewGuid(), JobCategory.BE,
            new[] { "Q1" },
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) });

        await svc.CreateCampaignSessionAsync(Guid.NewGuid(), req);
        await svc.CreateCampaignSessionAsync(Guid.NewGuid(), req);

        await using var read = t.NewContext();
        Assert.Equal(1, await read.RubricCriteria.CountAsync(c => c.CampaignId == campaignId));
        Assert.Equal(2, await read.PracticeSessions.CountAsync(s => s.CampaignId == campaignId));
    }

    // D2(a): create-or-get (candidateId, campaignId) lần đầu → tạo session + materialize rubric_criteria.
    [Fact]
    public async Task GetOrCreate_LanDau_TaoSession_VaMaterializeCriteria()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var svc = Build(t);

        var req = new CreateCampaignSessionRequest(
            campaignId, Guid.NewGuid(), JobCategory.BE, new[] { "Q1", "Q2" },
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) });

        var res = await svc.GetOrCreateCampaignSessionAsync(candidate, req);

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        Assert.Equal(2, res.Questions.Count);

        await using var read = t.NewContext();
        Assert.Equal(1, await read.PracticeSessions.CountAsync(s => s.CampaignId == campaignId));
        Assert.Equal(1, await read.RubricCriteria.CountAsync(c => c.CampaignId == campaignId));
    }

    // D2(b): gọi lại cùng (candidateId, campaignId) khi session chưa terminal → CÙNG sessionId, không đẻ trùng.
    [Fact]
    public async Task GetOrCreate_GoiLai_TraCungSession_KhongDeTrung()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var svc = Build(t);

        var req = new CreateCampaignSessionRequest(
            campaignId, Guid.NewGuid(), JobCategory.BE, new[] { "Q1" },
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) });

        var first = await svc.GetOrCreateCampaignSessionAsync(candidate, req);
        var second = await svc.GetOrCreateCampaignSessionAsync(candidate, req);

        Assert.Equal(first.Id, second.Id);

        await using var read = t.NewContext();
        Assert.Equal(1, await read.PracticeSessions.CountAsync(s => s.CampaignId == campaignId));
    }

    [Fact]
    public async Task GetOrCreate_OverCapacity_ResumeReturnsExisting_AndNewSessionDoesNotReserve()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var candidate = Guid.NewGuid();
        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        var existing = TestDb.Session(candidate, SessionStatus.InProgress, campaignId: campaignId);
        t.Db.Add(existing);
        await t.Db.SaveChangesAsync();
        var req = new CreateCampaignSessionRequest(campaignId, Guid.NewGuid(), JobCategory.BE, ["Q1"],
            [new CampaignCriterionInput("Communication", null, 1m, 5)]);

        var resumed = await Build(t, maxConcurrentSessions: 1, credits).GetOrCreateCampaignSessionAsync(candidate, req);
        Assert.Equal(existing.Id, resumed.Id);
        credits.Verify(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        await Assert.ThrowsAsync<CapacityExceededException>(() =>
            Build(t, maxConcurrentSessions: 1, credits).GetOrCreateCampaignSessionAsync(Guid.NewGuid(), req));
        credits.Verify(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // D2(c): khác candidate cùng campaign → session riêng (mỗi ứng viên 1 bài); criteria không nhân đôi.
    [Fact]
    public async Task GetOrCreate_KhacCandidate_SessionRieng()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var svc = Build(t);
        var req = new CreateCampaignSessionRequest(
            campaignId, Guid.NewGuid(), JobCategory.BE, new[] { "Q1" },
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) });

        var a = await svc.GetOrCreateCampaignSessionAsync(Guid.NewGuid(), req);
        var b = await svc.GetOrCreateCampaignSessionAsync(Guid.NewGuid(), req);

        Assert.NotEqual(a.Id, b.Id);
        await using var read = t.NewContext();
        Assert.Equal(2, await read.PracticeSessions.CountAsync(s => s.CampaignId == campaignId));
        Assert.Equal(1, await read.RubricCriteria.CountAsync(c => c.CampaignId == campaignId));
    }

    // D2(d): controller internal — X-Internal-Token sai → 401 (không chạm service).
    [Fact]
    public async Task InternalController_SaiToken_Tra401()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "test-internal-token"
        }).Build();

        var practice = new Mock<IPracticeService>();
        var controller = new InternalSessionsController(
            practice.Object, config, NullLogger<InternalSessionsController>.Instance);

        var req = new CreateCampaignSessionInternalRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE", new[] { "Q1" },
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) });

        var result = await controller.CreateOrGetCampaignSession(req, token: "wrong-token", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        practice.Verify(p => p.GetOrCreateCampaignSessionAsync(
            It.IsAny<Guid>(), It.IsAny<CreateCampaignSessionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── INT-17b: đường B2B phải ép `MaxFollowUps = 0` ở chế độ chuỗi (đối xứng B2C) ──────────────
    // Trước bản vá, `MaxFollowUps` lấy thẳng giá trị HR khai. Vì `AnswerService` đếm `followUpCount`
    // trên MỌI câu non-Seed của cả buổi, trần theo BUỔI bó chặt hơn trần theo CÂU ⇒ chuỗi chết giữa
    // chừng, và chết ở đâu thì phụ thuộc THỨ TỰ TRẢ LỜI ⇒ hai ứng viên cùng campaign nhận số câu
    // khác nhau trong khi điểm vẫn xếp hạng chung (CAMP-10).

    private static CreateCampaignSessionRequest CampaignReq(
        Guid campaignId, string[] questions, int? maxDeep, int? maxFollowUps, int? maxQuestions = 20) =>
        new(campaignId, Guid.NewGuid(), JobCategory.BE,
            Questions: questions,
            Criteria: new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) },
            AdaptiveEnabled: true,
            MaxFollowUps: maxFollowUps,
            MaxQuestions: maxQuestions,
            MaxDeepPerQuestion: maxDeep);

    [Fact]
    public async Task CreateCampaignSession_CheDoChuoi_EpMaxFollowUpsVe0()
    {
        using var t = new TestDb();
        var svc = Build(t);

        var res = await svc.CreateCampaignSessionAsync(
            Guid.NewGuid(), CampaignReq(Guid.NewGuid(), new[] { "Q1", "Q2" }, maxDeep: 3, maxFollowUps: 3));

        await using var read = t.NewContext();
        var s = await read.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.Equal(0, s.MaxFollowUps);        // ← trần BUỔI tắt
        Assert.Equal(3, s.MaxDeepPerQuestion);  // ← trần theo CÂU giữ nguyên
    }

    [Fact]
    public async Task CreateCampaignSession_KillSwitch_GiuNguyenMaxFollowUps()
    {
        // Đối chứng: campaign KHÔNG dùng chế độ chuỗi (maxDeep=0) thì hành vi phải y hệt trước —
        // guard là CÓ ĐIỀU KIỆN, không phải ép mù. Thiếu test này thì một fix sai (ép 0 vô điều kiện)
        // vẫn làm test trên xanh.
        using var t = new TestDb();
        var svc = Build(t);

        var res = await svc.CreateCampaignSessionAsync(
            Guid.NewGuid(), CampaignReq(Guid.NewGuid(), new[] { "Q1" }, maxDeep: 0, maxFollowUps: 3));

        await using var read = t.NewContext();
        var s = await read.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.Equal(3, s.MaxFollowUps);
        Assert.Equal(0, s.MaxDeepPerQuestion);
    }

    [Fact]
    public async Task CreateCampaignSession_CheDoChuoi_MoiCauGocDuocDaoSauDayDu_KhongLechTheoThuTu()
    {
        // Test HÀNH VI — cái đáng giá nhất. Hai test trên chỉ bắt "con dấu" trên entity; test này chứng
        // minh hệ quả công bằng: MỌI câu gốc đều đào sâu đủ trần, bất kể trả lời theo thứ tự nào.
        // Trước bản vá: phân bố 2/1/0/0 (câu gốc trả lời sau cạn ngân sách buổi).
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var svc = Build(t);

        var created = await svc.CreateCampaignSessionAsync(
            candidate, CampaignReq(Guid.NewGuid(), new[] { "G1", "G2", "G3", "G4" }, maxDeep: 2, maxFollowUps: 3));

        var decider = new Mock<IAiServiceInterviewDecider>();
        decider.Setup(x => x.DecideNextAsync(It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new DecideNextResult("follow_up", "Đào sâu", "ts", null));

        var publisher = new Mock<IScoringJobPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync("answer-audio/x.webm");
        var answers = new AnswerService(
            t.Db, storage.Object, publisher.Object, new Mock<ISessionScoringNotifier>().Object,
            TestDb.ScoringOpts(), NullLogger<AnswerService>.Instance, decider.Object,
            Options.Create(new AdaptiveOptions { MaxFailuresPerSession = 3 }));

        // Trả lời XEN KẼ: mỗi vòng trả lời câu chưa-có-answer có orderNo nhỏ nhất — mô phỏng ứng viên
        // đi tuần tự qua danh sách, tức thứ tự bất lợi nhất cho ngân sách theo buổi.
        for (var round = 0; round < 12; round++)
        {
            await using var scan = t.NewContext();
            var next = await scan.PracticeQuestions.AsNoTracking()
                .Where(q => q.SessionId == created.Id)
                .Where(q => !scan.PracticeAnswers.Any(a => a.QuestionId == q.Id))
                .OrderBy(q => q.OrderNo).FirstOrDefaultAsync();
            if (next is null) break;
            using var audio = new MemoryStream(new byte[] { 1 });
            await answers.UploadAnswerAsync(created.Id, next.Id, candidate, audio, "audio/webm", 30);
        }

        await using var read = t.NewContext();
        var byRoot = await read.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == created.Id && q.Kind != QuestionKind.Seed)
            .GroupBy(q => q.RootQuestionId)
            .Select(g => new { Root = g.Key, Depth = g.Count() })
            .ToListAsync();

        Assert.Equal(4, byRoot.Count);                       // cả 4 câu gốc đều có chuỗi
        Assert.All(byRoot, x => Assert.Equal(2, x.Depth));   // và đều đủ trần 2 — không 2/1/0/0
    }
}
