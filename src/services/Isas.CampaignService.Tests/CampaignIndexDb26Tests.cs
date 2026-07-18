using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Isas.CampaignService.Tests;

/// <summary>
/// DB26 — index cho các predicate THẬT sự chạy (đọc từ code, không đoán):
///  • campaign_membership(session_id) partial — RankingEventHandler tra theo session_id mỗi event SessionScored.
///  • audit_logs(org_id, at) + (actor_user_id, at) — audit đọc theo org/người, không chỉ theo entity.
///  • campaigns(org_id, created_at, id) — phủ trọn khoá sắp xếp keyset của list Employer (DB31).
///  • campaign_rankings(campaign_id) — rút `total_score` chết ở đuôi (E5 sort in-memory, không dùng index).
/// CampaignTestDb.EnsureCreated() sinh DDL thật → index map sai cột thì constructor ném ngay.
/// </summary>
public class CampaignIndexDb26Tests
{
    private static IIndex? FindIndex(CampaignDbContext db, Type clr, params string[] props)
        => db.Model.FindEntityType(clr)!
              .GetIndexes()
              .SingleOrDefault(ix => ix.Properties.Select(p => p.Name).SequenceEqual(props));

    [Fact]
    public void Membership_co_partial_index_session_id()
    {
        using var tdb = new CampaignTestDb();

        var index = FindIndex(tdb.Db, typeof(CampaignMembership), nameof(CampaignMembership.SessionId));

        Assert.NotNull(index);
        Assert.False(index!.IsUnique);                                   // task hiệu năng, KHÔNG siết ràng buộc
        Assert.Equal("session_id IS NOT NULL", index.GetFilter());       // partial: bỏ qua membership chưa Start
    }

    // Index chỉ có giá trị nếu nó phục vụ đúng truy vấn đang chạy — chứng minh bằng đường đọc thật
    // (predicate y hệt RankingEventHandler.MarkMembershipCompletedAsync) chạy được trên schema đã sinh.
    [Fact]
    public async Task Membership_tra_theo_session_id_chay_dung_tren_schema_that()
    {
        using var tdb = new CampaignTestDb();
        var campaign = CampaignTestDb.NewCampaign(Guid.NewGuid());
        tdb.Db.Campaigns.Add(campaign);

        var sid = Guid.NewGuid();
        tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(campaign.Id, Guid.NewGuid(), sessionId: sid));
        tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(campaign.Id, Guid.NewGuid()));   // chưa Start → session_id NULL
        await tdb.Db.SaveChangesAsync();

        using var db = tdb.NewContext();
        var hit = await db.CampaignMemberships.FirstOrDefaultAsync(m => m.SessionId == sid);

        Assert.NotNull(hit);
        // Row session_id NULL không lọt vào (predicate `= @sid` không khớp NULL) → partial index an toàn.
        Assert.Equal(sid, hit!.SessionId);
    }

    [Fact]
    public void AuditLog_co_index_theo_org_va_theo_actor()
    {
        using var tdb = new CampaignTestDb();

        Assert.NotNull(FindIndex(tdb.Db, typeof(AuditLog), nameof(AuditLog.OrgId), nameof(AuditLog.At)));
        Assert.NotNull(FindIndex(tdb.Db, typeof(AuditLog), nameof(AuditLog.ActorUserId), nameof(AuditLog.At)));
        // Index cũ theo entity giữ nguyên (không đánh đổi câu hỏi audit này lấy câu hỏi audit kia).
        Assert.NotNull(FindIndex(tdb.Db, typeof(AuditLog), nameof(AuditLog.EntityId), nameof(AuditLog.At)));
    }

    [Fact]
    public void Campaign_index_org_created_at_co_duoi_id()
    {
        using var tdb = new CampaignTestDb();

        var index = FindIndex(tdb.Db, typeof(Campaign),
            nameof(Campaign.OrgId), nameof(Campaign.CreatedAt), nameof(Campaign.Id));
        Assert.NotNull(index);   // phủ trọn ORDER BY created_at DESC, id DESC của keyset DB31

        // Index cũ (org_id, created_at) không còn — superset đã thay, giữ lại = index thừa.
        Assert.Null(FindIndex(tdb.Db, typeof(Campaign), nameof(Campaign.OrgId), nameof(Campaign.CreatedAt)));
    }

    [Fact]
    public void Ranking_index_chi_con_campaign_id_khong_con_total_score()
    {
        using var tdb = new CampaignTestDb();

        Assert.NotNull(FindIndex(tdb.Db, typeof(CampaignRanking), nameof(CampaignRanking.CampaignId)));
        // Regression guard: `total_score` ở đuôi là cột chết (E5 sắp in-memory theo override_score ?? total_score)
        // → mỗi upsert điểm phải sửa index vô ích. Đừng thêm lại nếu không có ORDER BY total_score Ở SQL.
        Assert.Null(FindIndex(tdb.Db, typeof(CampaignRanking),
            nameof(CampaignRanking.CampaignId), nameof(CampaignRanking.TotalScore)));
    }
}
