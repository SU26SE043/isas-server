using Isas.CampaignService.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Isas.CampaignService.Tests;

// DB13/DB9: entity con (Question/Criterion/Invitation/Candidate + DB9: Ranking/Flag) có FK required
// tới Campaign (đã soft-delete query filter). Nếu con KHÔNG có query filter riêng → EF phát
// PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning + đọc "orphan-in-view"
// (con của campaign đã soft-delete). Test khoá: model build không còn warning + con bị lọc theo campaign.
public class CampaignChildQueryFilterDb13Tests
{
    // Model finalize với warning cấu hình THROW: nếu 1 entity con thiếu filter → ném → test fail.
    // Sau khi thêm nav-filter cho cả 4 con → model build sạch, không ném.
    [Fact]
    public void Model_khong_con_RequiredNavigation_QueryFilter_warning()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<CampaignDbContext>()
            .UseSqlite(conn)
            .ConfigureWarnings(w => w.Throw(
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning))
            .Options;

        using var db = new CampaignDbContext(options);
        // Truy cập Model → buộc finalize + validate; warning (nếu có) ném ở đây.
        var ex = Record.Exception(() => _ = db.Model);
        Assert.Null(ex);
    }

    [Fact]
    public async Task Con_cua_campaign_soft_deleted_bi_loc_khoi_query_thuong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        tdb.Db.Campaigns.Add(camp);

        var qId = Guid.NewGuid();
        var crId = Guid.NewGuid();
        var invId = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var memId = Guid.NewGuid();
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = qId, CampaignId = camp.Id, QuestionText = "Q1", CreatedAt = DateTime.UtcNow
        });
        tdb.Db.CampaignCriteria.Add(new CampaignCriterion
        {
            Id = crId, CampaignId = camp.Id, Name = "Crit1", Weight = 1m,
            OrderNo = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.CampaignInvitations.Add(new CampaignInvitation
        {
            Id = invId, CampaignId = camp.Id,
            TokenHash = Isas.CampaignService.Services.InvitationTokens.Hash("tok-1"), Email = "a@b.co",
            ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow
        });
        // DB16 — cả cv_submission và campaign_membership đều required-nav Campaign (soft-delete filter).
        tdb.Db.CvSubmissions.Add(new CvSubmission
        {
            Id = cvId, CampaignId = camp.Id, Email = "c@b.co",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = memId, CampaignId = camp.Id, CandidateId = Guid.NewGuid(), CvSubmissionId = cvId,
            Status = MembershipStatus.Joined, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        // Soft-delete campaign.
        camp.DeletedAt = DateTime.UtcNow;
        await tdb.Db.SaveChangesAsync();

        // Query THƯỜNG (global/nav filter) → con của campaign đã xoá đều bị ẩn.
        using (var read = tdb.NewContext())
        {
            Assert.Empty(await read.CampaignQuestions.ToListAsync());
            Assert.Empty(await read.CampaignCriteria.ToListAsync());
            Assert.Empty(await read.CampaignInvitations.ToListAsync());
            Assert.Empty(await read.CvSubmissions.ToListAsync());
            Assert.Empty(await read.CampaignMemberships.ToListAsync());
        }

        // IgnoreQueryFilters → row con vẫn còn (chỉ ẩn ở view, không hard-delete).
        using (var raw = tdb.NewContext())
        {
            Assert.NotNull(await raw.CampaignQuestions.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == qId));
            Assert.NotNull(await raw.CampaignCriteria.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == crId));
            Assert.NotNull(await raw.CampaignInvitations.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == invId));
            Assert.NotNull(await raw.CvSubmissions.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == cvId));
            Assert.NotNull(await raw.CampaignMemberships.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == memId));
        }
    }

    [Fact]
    public async Task Con_cua_campaign_song_van_hien_trong_query_thuong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);   // KHÔNG soft-delete
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, QuestionText = "Q1", CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        using var read = tdb.NewContext();
        Assert.Single(await read.CampaignQuestions.ToListAsync());
    }

    // DB9: campaign_rankings + session_flags cũng có nav-based soft-delete filter (required nav tới
    // Campaign, thêm ở DB9). Soft-delete campaign → cả 2 read-model của nó bị ẩn ở query thường;
    // IgnoreQueryFilters vẫn thấy (chỉ ẩn ở view, không hard-delete).
    [Fact]
    public async Task Rankings_va_flags_cua_campaign_soft_deleted_bi_loc()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);

        var rankId = Guid.NewGuid();
        var flagId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        tdb.Db.CampaignRankings.Add(new CampaignRanking
        {
            Id = rankId, CampaignId = camp.Id, CandidateId = Guid.NewGuid(),
            SessionId = sessionId, TotalScore = 80.00m, UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.SessionFlags.Add(new SessionFlag
        {
            Id = flagId, CampaignId = camp.Id, SessionId = sessionId,
            CandidateId = Guid.NewGuid(), SignalType = "tab_switch", DetectedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        // Soft-delete campaign.
        camp.DeletedAt = DateTime.UtcNow;
        await tdb.Db.SaveChangesAsync();

        // Query THƯỜNG → ranking + flag của campaign đã xoá đều bị ẩn (nav-based filter join campaigns).
        using (var read = tdb.NewContext())
        {
            Assert.Empty(await read.CampaignRankings.ToListAsync());
            Assert.Empty(await read.SessionFlags.ToListAsync());
        }

        // IgnoreQueryFilters → row con vẫn còn (chỉ ẩn ở view, không hard-delete).
        using (var raw = tdb.NewContext())
        {
            Assert.NotNull(await raw.CampaignRankings.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == rankId));
            Assert.NotNull(await raw.SessionFlags.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == flagId));
        }
    }
}
