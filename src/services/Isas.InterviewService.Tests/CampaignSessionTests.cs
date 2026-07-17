using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// I1: tạo session B2B (campaign_id) + materialize tiêu chí campaign → rubric_criteria(campaign_id).
/// Câu hỏi + tiêu chí do Campaign cấp; không gọi AI (storage/generator mock, không dùng).
/// </summary>
public class CampaignSessionTests
{
    private static PracticeService Build(TestDb t)
    {
        var scoringNotifier = new Mock<ISessionScoringNotifier>();
        scoringNotifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // BK14: B2B reserve ví ORG khi tạo session → mock trả reservation hợp lệ cho mọi ownerType.
        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(t.Db, new Mock<IStorageService>().Object,
               new Mock<IAiServiceQuestionGenerator>().Object,
               scoringNotifier.Object,
               reservation.Object,
               NullLogger<PracticeService>.Instance);
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
}
