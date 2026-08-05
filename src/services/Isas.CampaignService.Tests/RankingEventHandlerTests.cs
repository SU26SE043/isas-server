using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

// E4: RankingEventHandler.HandleSessionScoredAsync — consume SessionScored → upsert campaign_rankings.
// Test trực tiếp handler (fake message, không cần RabbitMQ thật — như task yêu cầu).
public class RankingEventHandlerTests
{
    private static RankingEventHandler NewHandler(CampaignDbContext db) =>
        new(db, Mock.Of<ILogger<RankingEventHandler>>());

    [Fact]
    public async Task SessionAbandoned_InProgress_DanhDauAbandonedVaNhaDeadline()
    {
        using var tdb = new CampaignTestDb();
        var campaign = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var sessionId = Guid.NewGuid();
        var membership = CampaignTestDb.NewMembership(campaign.Id, Guid.NewGuid(), sessionId: sessionId,
            interviewStatus: InterviewProgressStatus.InProgress);
        membership.InterviewDeadlineAt = DateTime.UtcNow.AddHours(1);
        tdb.Db.AddRange(campaign, membership);
        await tdb.Db.SaveChangesAsync();

        await NewHandler(tdb.NewContext()).HandleSessionAbandonedAsync(new SessionAbandonedMessage
        {
            SessionId = sessionId, CampaignId = campaign.Id, CandidateId = membership.CandidateId!.Value,
            Reason = "expired_no_answer", AbandonedAt = DateTime.UtcNow
        });

        using var check = tdb.NewContext();
        var after = await check.CampaignMemberships.SingleAsync();
        Assert.Equal(InterviewProgressStatus.Abandoned, after.InterviewStatus);
        Assert.Equal(sessionId, after.SessionId);
        Assert.Null(after.InterviewDeadlineAt);
    }

    [Fact]
    public async Task SessionAbandoned_KhongHaCapCompleted()
    {
        using var tdb = new CampaignTestDb();
        var campaign = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var membership = CampaignTestDb.NewMembership(campaign.Id, Guid.NewGuid(), sessionId: Guid.NewGuid(),
            interviewStatus: InterviewProgressStatus.Completed);
        tdb.Db.AddRange(campaign, membership);
        await tdb.Db.SaveChangesAsync();

        await NewHandler(tdb.NewContext()).HandleSessionAbandonedAsync(new SessionAbandonedMessage
        {
            SessionId = membership.SessionId!.Value, CampaignId = campaign.Id, CandidateId = membership.CandidateId!.Value
        });

        using var check = tdb.NewContext();
        Assert.Equal(InterviewProgressStatus.Completed, (await check.CampaignMemberships.SingleAsync()).InterviewStatus);
    }

    // E4(a): SessionScored B2B (campaignId có giá trị) → 1 row campaign_rankings với total_score có trọng số.
    [Fact]
    public async Task SessionScored_B2B_tao_1_row_ranking_voi_diem_co_trong_so()
    {
        using var tdb = new CampaignTestDb();
        // DB9: campaign_rankings.campaign_id → campaigns.id (FK + query filter). Seed 1 campaign THẬT
        // để ranking không bị lọc (query filter join campaigns) + FK thoả (đúng ngữ nghĩa B2B).
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var campaignId = camp.Id;
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var handler = NewHandler(tdb.NewContext());
        await handler.HandleSessionScoredAsync(new SessionScoredMessage
        {
            SessionId = sessionId,
            CampaignId = campaignId,
            CandidateId = candidateId,
            TotalScore = 82.50m,
            ScoredAt = DateTime.UtcNow
        });

        using var check = tdb.NewContext();
        var rows = await check.CampaignRankings.Where(r => r.SessionId == sessionId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(campaignId, rows[0].CampaignId);
        Assert.Equal(candidateId, rows[0].CandidateId);
        Assert.Equal(82.50m, rows[0].TotalScore);
    }

    // E4(b): cùng session_id gửi event 2 lần (redelivery/duplicate) → vẫn chỉ 1 row (upsert idempotent),
    // giá trị mới nhất được ghi đè (không nhân đôi row).
    [Fact]
    public async Task SessionScored_cung_session_id_gui_2_lan_van_1_row()
    {
        using var tdb = new CampaignTestDb();
        // DB9: seed campaign THẬT — nếu không, query filter (join campaigns) ẩn ranking → handler
        // upsert lần 2 không thấy row cũ → INSERT thứ 2 → vi phạm UNIQUE(session_id). Có campaign →
        // upsert idempotent đúng.
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var campaignId = camp.Id;
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var evt1 = new SessionScoredMessage
        {
            SessionId = sessionId,
            CampaignId = campaignId,
            CandidateId = candidateId,
            TotalScore = 70.00m,
            ScoredAt = DateTime.UtcNow
        };
        var evt2 = new SessionScoredMessage
        {
            SessionId = sessionId,   // CÙNG session_id
            CampaignId = campaignId,
            CandidateId = candidateId,
            TotalScore = 70.00m,     // event gửi lại giữ nguyên điểm (redelivery)
            ScoredAt = DateTime.UtcNow
        };

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(evt1);
        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(evt2);

        using var check = tdb.NewContext();
        var rows = await check.CampaignRankings.Where(r => r.SessionId == sessionId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(70.00m, rows[0].TotalScore);
    }

    // E4(c): SessionScored B2C (campaignId = null) → E4 chỉ xếp hạng B2B, KHÔNG tạo row nào.
    [Fact]
    public async Task SessionScored_B2C_campaignId_null_khong_tao_row()
    {
        using var tdb = new CampaignTestDb();
        var sessionId = Guid.NewGuid();

        var handler = NewHandler(tdb.NewContext());
        await handler.HandleSessionScoredAsync(new SessionScoredMessage
        {
            SessionId = sessionId,
            CampaignId = null,   // B2C
            CandidateId = Guid.NewGuid(),
            TotalScore = 90.00m,
            ScoredAt = DateTime.UtcNow
        });

        using var check = tdb.NewContext();
        Assert.Empty(await check.CampaignRankings.ToListAsync());
    }

    // D2: SessionScored → membership interview_status = Completed (khớp session_id) + ranking vẫn upsert.
    [Fact]
    public async Task SessionScored_DanhDauMembershipCompleted_VaVanUpsertRanking()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        // Campaign thật (campaign_candidates có FK → campaigns)
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        var campaignId = camp.Id;
        tdb.Db.Campaigns.Add(camp);

        // membership đã start (InProgress, gắn session_id) — DB16: bảng campaign_membership
        tdb.Db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = candidateId,
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow, SessionId = sessionId,
            InterviewStatus = InterviewProgressStatus.InProgress,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        await NewHandler(tdb.NewContext()).HandleSessionScoredAsync(new SessionScoredMessage
        {
            SessionId = sessionId,
            CampaignId = campaignId,
            CandidateId = candidateId,
            TotalScore = 77.00m,
            ScoredAt = DateTime.UtcNow
        });

        using var check = tdb.NewContext();
        Assert.Single(await check.CampaignRankings.Where(r => r.SessionId == sessionId).ToListAsync());
        var membership = await check.CampaignMemberships.SingleAsync(m => m.SessionId == sessionId);
        Assert.Equal(InterviewProgressStatus.Completed, membership.InterviewStatus);
    }
}
