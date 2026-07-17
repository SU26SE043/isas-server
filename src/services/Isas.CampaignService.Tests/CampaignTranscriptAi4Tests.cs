using System.Security.Claims;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// AI4 — HR xem transcript + nhận xét AI per-criterion + cờ needs_review 1 buổi (đối chiếu điểm ranking).
/// Consumer phía Campaign: gating GIỐNG override (org sở hữu campaign + ranking row thuộc campaign) → ngoài
/// org / session chưa chấm = 404 (KHÔNG gọi Interview); hợp lệ → delegate ICampaignSessionClient trả detail.
/// Client (Interview) lỗi → DownstreamServiceException → controller 502. Stub client bằng Moq (không HTTP thật).
/// </summary>
public class CampaignTranscriptAi4Tests
{
    private static CampaignSvc NewService(CampaignDbContext db, ICampaignSessionClient client) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>(), client);

    private static CampaignController NewController(CampaignDbContext db, Guid orgId, ICampaignSessionClient client)
    {
        var controller = new CampaignController(
            NewService(db, client), Mock.Of<ICvScreeningService>(), Mock.Of<ILogger<CampaignController>>());
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("org_id", orgId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static Campaign SeedCampaign(CampaignDbContext db, Guid orgId)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static CampaignRanking SeedRanking(CampaignDbContext db, Guid campaignId, Guid? sessionId = null)
    {
        var r = new CampaignRanking
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = Guid.NewGuid(),
            SessionId = sessionId ?? Guid.NewGuid(),
            TotalScore = 80m,
            UpdatedAt = DateTime.UtcNow
        };
        db.CampaignRankings.Add(r);
        db.SaveChanges();
        return r;
    }

    private static SessionTranscriptResponse SampleDetail(Guid sessionId) => new()
    {
        SessionId = sessionId,
        Questions = new List<TranscriptQuestion>
        {
            new()
            {
                QuestionId = Guid.NewGuid(),
                OrderNo = 1,
                Content = "Giải thích dependency injection?",
                Transcript = "Ứng viên nói về constructor injection.",
                NeedsReview = true,
                Scores = new List<TranscriptCriterionScore>
                {
                    new() { CriterionId = Guid.NewGuid(), Score = 4m, Reasoning = "Trích transcript: 'constructor injection'." }
                }
            }
        }
    };

    private static Mock<ICampaignSessionClient> StubClient(Guid sessionId, SessionTranscriptResponse detail)
    {
        var client = new Mock<ICampaignSessionClient>();
        client.Setup(c => c.GetSessionTranscriptAsync(sessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(detail);
        return client;
    }

    // ── SERVICE — gating + delegate ──────────────────────────────────────────

    [Fact]
    public async Task Transcript_OwningOrg_ReturnsDetail_FromClient()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id);
        var detail = SampleDetail(ranking.SessionId);
        var client = StubClient(ranking.SessionId, detail);

        var result = await NewService(tdb.NewContext(), client.Object)
            .GetSessionTranscriptAsync(orgId, campaign.Id, ranking.SessionId, default);

        Assert.Equal(ranking.SessionId, result.SessionId);
        var q = Assert.Single(result.Questions);
        Assert.Contains("constructor injection", q.Transcript);
        Assert.True(q.NeedsReview);
        Assert.Contains("constructor injection", Assert.Single(q.Scores).Reasoning);
        client.Verify(c => c.GetSessionTranscriptAsync(ranking.SessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Transcript_DifferentOrg_Throws404_WithoutCallingInterview()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaign(tdb.Db, Guid.NewGuid());
        var ranking = SeedRanking(tdb.Db, campaign.Id);
        var client = StubClient(ranking.SessionId, SampleDetail(ranking.SessionId));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext(), client.Object)
                .GetSessionTranscriptAsync(Guid.NewGuid() /* org khác */, campaign.Id, ranking.SessionId, default));

        client.Verify(c => c.GetSessionTranscriptAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Transcript_UnknownSession_Throws404_WithoutCallingInterview()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);   // KHÔNG seed ranking cho session này
        var unknownSession = Guid.NewGuid();
        var client = StubClient(unknownSession, SampleDetail(unknownSession));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext(), client.Object)
                .GetSessionTranscriptAsync(orgId, campaign.Id, unknownSession, default));

        client.Verify(c => c.GetSessionTranscriptAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── CONTROLLER — HTTP mapping (200 / 404 / 502) ──────────────────────────

    [Fact]
    public async Task Controller_OwningOrg_Returns200_WithDetail()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id);
        var client = StubClient(ranking.SessionId, SampleDetail(ranking.SessionId));

        var result = await NewController(tdb.NewContext(), orgId, client.Object)
            .GetSessionTranscript(campaign.Id, ranking.SessionId, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<SessionTranscriptResponse>(ok.Value);
        Assert.Equal(ranking.SessionId, detail.SessionId);
    }

    [Fact]
    public async Task Controller_DifferentOrg_Returns404()
    {
        using var tdb = new CampaignTestDb();
        var campaign = SeedCampaign(tdb.Db, Guid.NewGuid());
        var ranking = SeedRanking(tdb.Db, campaign.Id);
        var client = StubClient(ranking.SessionId, SampleDetail(ranking.SessionId));

        var result = await NewController(tdb.NewContext(), Guid.NewGuid() /* org khác */, client.Object)
            .GetSessionTranscript(campaign.Id, ranking.SessionId, default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Controller_InterviewDown_Returns502()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, orgId);
        var ranking = SeedRanking(tdb.Db, campaign.Id);

        var client = new Mock<ICampaignSessionClient>();
        client.Setup(c => c.GetSessionTranscriptAsync(ranking.SessionId, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new DownstreamServiceException("Interview down"));

        var result = await NewController(tdb.NewContext(), orgId, client.Object)
            .GetSessionTranscript(campaign.Id, ranking.SessionId, default);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
    }
}
