using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Pagination;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// GET /campaign/my-campaigns — dashboard ứng viên, nay keyset-paged theo (CreatedAt DESC, Id DESC)
/// của membership và lọc soft-delete campaign Ở SQL (trước đây lấy hết rồi mới bỏ campaign đã xoá
/// trong C#, nên campaign đã xoá vẫn "chiếm chỗ" nếu có phân trang).
/// </summary>
public class MyCampaignsPagingTests
{
    private static readonly Guid Candidate = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ParticipationService NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IAuthProvisionClient>(), Mock.Of<ICampaignSessionClient>(),
            NullLogger<ParticipationService>.Instance);

    private static Campaign SeedJoined(CampaignTestDb tdb, string title, DateTime createdAt, bool deleted = false)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Title = title;
        if (deleted)
            camp.DeletedAt = DateTime.UtcNow;
        tdb.Db.Campaigns.Add(camp);

        var m = CampaignTestDb.NewMembership(camp.Id, Candidate);
        m.CreatedAt = createdAt;
        m.JoinedAt = createdAt;
        tdb.Db.CampaignMemberships.Add(m);
        return camp;
    }

    [Fact]
    public async Task KhongCursor_MoiNhatTruoc_GiuHanhViCu()
    {
        using var tdb = new CampaignTestDb();
        var t0 = DateTime.UtcNow;
        SeedJoined(tdb, "cu", t0.AddHours(-3));
        SeedJoined(tdb, "giua", t0.AddHours(-2));
        SeedJoined(tdb, "moi", t0.AddHours(-1));
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetMyCampaignsAsync(Candidate, null, null, default);

        Assert.Equal(new[] { "moi", "giua", "cu" }, page.Items.Select(c => c.Title));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task PhanTrang_KhongTrungKhongSot()
    {
        using var tdb = new CampaignTestDb();
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            SeedJoined(tdb, $"c{i}", t0.AddHours(-i));   // c0 mới nhất
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        var p1 = await svc.GetMyCampaignsAsync(Candidate, null, 2, default);
        Assert.Equal(new[] { "c0", "c1" }, p1.Items.Select(c => c.Title));
        Assert.NotNull(p1.NextCursor);

        var p2 = await svc.GetMyCampaignsAsync(Candidate, p1.NextCursor, 2, default);
        Assert.Equal(new[] { "c2", "c3" }, p2.Items.Select(c => c.Title));

        var p3 = await svc.GetMyCampaignsAsync(Candidate, p2.NextCursor, 2, default);
        Assert.Equal(new[] { "c4" }, p3.Items.Select(c => c.Title));
        Assert.Null(p3.NextCursor);
    }

    [Fact]
    public async Task TrungCreatedAt_TieBreakTheoId_KhongLapKhongMat()
    {
        using var tdb = new CampaignTestDb();
        var same = DateTime.UtcNow.AddMinutes(-10);
        SeedJoined(tdb, "a", same);
        SeedJoined(tdb, "b", same);
        SeedJoined(tdb, "c", same);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var seen = new List<string>();
        string? cursor = null;

        for (var guard = 0; guard < 6; guard++)
        {
            var page = await svc.GetMyCampaignsAsync(Candidate, cursor, 1, default);
            seen.AddRange(page.Items.Select(c => c.Title!));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    // Soft-delete lọc ở SQL: trang đầu (limit 2) phải ĐẦY 2 campaign còn sống, dù 2 campaign đã xoá
    // nằm chen ngay đầu danh sách. Lọc sau phân trang thì trang này chỉ còn 0 dòng.
    [Fact]
    public async Task SoftDelete_LocODuoiSql_TrangVanDay()
    {
        using var tdb = new CampaignTestDb();
        var t0 = DateTime.UtcNow;
        SeedJoined(tdb, "da-xoa-1", t0.AddMinutes(-1), deleted: true);
        SeedJoined(tdb, "da-xoa-2", t0.AddMinutes(-2), deleted: true);
        SeedJoined(tdb, "song-1", t0.AddMinutes(-3));
        SeedJoined(tdb, "song-2", t0.AddMinutes(-4));
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetMyCampaignsAsync(Candidate, null, 2, default);

        Assert.Equal(new[] { "song-1", "song-2" }, page.Items.Select(c => c.Title));
    }

    [Fact]
    public async Task CursorRac_VeTrangDau_KhongNo()
    {
        using var tdb = new CampaignTestDb();
        SeedJoined(tdb, "x", DateTime.UtcNow);
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetMyCampaignsAsync(Candidate, "khong-phai-base64!!", null, default);

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Limit_BiKepTran()
    {
        using var tdb = new CampaignTestDb();
        SeedJoined(tdb, "x", DateTime.UtcNow);
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetMyCampaignsAsync(Candidate, null, KeysetPaging.MaxLimit + 10_000, default);

        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    // Phân trang không được làm rơi mất owner-scope (membership của candidate khác không lọt).
    [Fact]
    public async Task KhongLanMembershipCuaCandidateKhac()
    {
        using var tdb = new CampaignTestDb();
        SeedJoined(tdb, "cua-toi", DateTime.UtcNow);

        var other = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        other.Title = "cua-nguoi-khac";
        tdb.Db.Campaigns.Add(other);
        tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(other.Id, Guid.NewGuid()));
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetMyCampaignsAsync(Candidate, null, null, default);

        Assert.Equal("cua-toi", Assert.Single(page.Items).Title);
    }
}
