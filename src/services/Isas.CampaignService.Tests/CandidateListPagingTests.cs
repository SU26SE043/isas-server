using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Pagination;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// GET /campaign/{id}/candidates — màn shortlist HR dùng nhiều nhất, nay keyset-paged + search +
/// sort đẩy xuống SQL. Trước đây `ToListAsync()` nạp TOÀN BỘ ứng viên của campaign rồi mới lọc/sắp
/// trong C#, mà trần `max_candidates` là `int?` ⇒ để trống là KHÔNG có trần nào.
/// </summary>
public class CandidateListPagingTests
{
    private static CvScreeningService NewService(CampaignDbContext db) =>
        new(db, Mock.Of<ICvScreeningPublisher>(),
            Mock.Of<IConfiguration>(), Mock.Of<ILogger<CvScreeningService>>());

    private static CvSubmission Seed(
        CampaignDbContext db, Guid campaignId, string? fullName, string? email, int? score,
        List<string>? skills = null, CvSubmissionStatus status = CvSubmissionStatus.Analyzed)
    {
        var cv = new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            FullName = fullName,
            Email = email,
            OverallMatchScore = score,
            Skills = skills,
            ParseStatus = CvParseStatus.Done,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.CvSubmissions.Add(cv);
        return cv;
    }

    private static Campaign SeedCampaign(CampaignTestDb tdb, Guid orgId)
    {
        var camp = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        return camp;
    }

    // ── sort=score (mặc định) ────────────────────────────────────────────────────────────

    // Không cursor = hành vi cũ: điểm cao trước, chưa-Analyzed (score null) xuống CUỐI.
    [Fact]
    public async Task ScoreSort_KhongCursor_DiemCaoTruoc_NullXuongCuoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        Seed(tdb.Db, camp.Id, "thap", "c@x.com", 40);
        Seed(tdb.Db, camp.Id, "chua-cham", "d@x.com", null, status: CvSubmissionStatus.Filtered);
        Seed(tdb.Db, camp.Id, "cao", "a@x.com", 90);
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetCandidatesAsync(org, camp.Id, null, null, null, null, null, null, null, default);

        Assert.Equal(new[] { "cao", "thap", "chua-cham" }, page.Items.Select(c => c.FullName));
        Assert.Null(page.NextCursor);   // < limit → hết trang (backward-compat, FE cũ không phải sửa)
    }

    // Phân trang theo điểm: nối tiếp không trùng không sót, kể cả khi có dòng score null ở đuôi.
    [Fact]
    public async Task ScoreSort_PhanTrang_KhongTrungKhongSot()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        foreach (var (name, score) in new (string, int?)[]
                 { ("s90", 90), ("s70", 70), ("s50", 50), ("s10", 10), ("null1", null), ("null2", null) })
            Seed(tdb.Db, camp.Id, name, $"{name}@x.com", score, status: CvSubmissionStatus.Filtered);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var seen = new List<string>();
        string? cursor = null;

        for (var guard = 0; guard < 10; guard++)
        {
            var page = await svc.GetCandidatesAsync(org, camp.Id, null, null, null, "score", null, cursor, 2, default);
            seen.AddRange(page.Items.Select(c => c.FullName!));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
        // Thứ tự toàn cục vẫn đúng qua ranh giới trang: điểm giảm dần, null cuối.
        Assert.Equal(new[] { "s90", "s70", "s50", "s10" }, seen.Take(4));
        Assert.Equal(new[] { "null1", "null2" }, seen.Skip(4).OrderBy(x => x));
    }

    // Trùng điểm (rất thường gặp: nhiều CV cùng 70) là ca duy nhất khiến keyset trượt nếu thiếu
    // tie-break theo Id — phải đủ 3 dòng, không lặp, không mất.
    [Fact]
    public async Task ScoreSort_TrungDiem_TieBreakTheoId_KhongLapKhongMat()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        Seed(tdb.Db, camp.Id, "a", "a@x.com", 70);
        Seed(tdb.Db, camp.Id, "b", "b@x.com", 70);
        Seed(tdb.Db, camp.Id, "c", "c@x.com", 70);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var seen = new List<string>();
        string? cursor = null;

        for (var guard = 0; guard < 6; guard++)
        {
            var page = await svc.GetCandidatesAsync(org, camp.Id, null, null, null, null, null, cursor, 1, default);
            seen.AddRange(page.Items.Select(c => c.FullName!));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    // ── sort=name ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NameSort_PhanTrang_TheoTen_KhongPhanBietHoaThuong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        Seed(tdb.Db, camp.Id, "Chau", "c@x.com", 10);
        Seed(tdb.Db, camp.Id, "an", "a@x.com", 90);      // chữ thường, phải đứng TRƯỚC "Binh"
        Seed(tdb.Db, camp.Id, "Binh", "b@x.com", 50);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var seen = new List<string>();
        string? cursor = null;

        for (var guard = 0; guard < 6; guard++)
        {
            var page = await svc.GetCandidatesAsync(org, camp.Id, null, null, null, "name", null, cursor, 2, default);
            seen.AddRange(page.Items.Select(c => c.FullName!));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(new[] { "an", "Binh", "Chau" }, seen);
    }

    // ── search ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_KhopTenHoacEmail_KhongPhanBietHoaThuong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        Seed(tdb.Db, camp.Id, "Nguyen Van An", "an.nguyen@corp.com", 90);
        Seed(tdb.Db, camp.Id, "Tran Thi Bich", "bich@corp.com", 80);
        Seed(tdb.Db, camp.Id, "Le Van Cuong", "cuong@other.com", 70);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // khớp TÊN (viết hoa khác dữ liệu)
        var byName = await svc.GetCandidatesAsync(org, camp.Id, null, null, null, null, "NGUYEN", null, null, default);
        Assert.Equal("Nguyen Van An", Assert.Single(byName.Items).FullName);

        // khớp EMAIL — cùng một tham số search
        var byEmail = await svc.GetCandidatesAsync(org, camp.Id, null, null, null, null, "other.com", null, null, default);
        Assert.Equal("Le Van Cuong", Assert.Single(byEmail.Items).FullName);

        // không khớp gì → rỗng, không nổ
        var none = await svc.GetCandidatesAsync(org, camp.Id, null, null, null, null, "zzz", null, null, default);
        Assert.Empty(none.Items);
    }

    // search PHẢI lọc trước khi phân trang: nếu lọc sau, trang 1 (limit 2) sẽ trả 2 dòng lẫn lộn rồi
    // mới bỏ bớt ⇒ mất dòng khớp nằm ngoài trang đầu.
    [Fact]
    public async Task Search_LocTruocPhanTrang_KhongMatDongKhopNamXa()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        for (var i = 0; i < 10; i++)
            Seed(tdb.Db, camp.Id, $"Nguoi {i}", $"user{i}@x.com", 100 - i);
        Seed(tdb.Db, camp.Id, "Muc Tieu", "target@x.com", 1);   // điểm thấp nhất → nằm CUỐI danh sách
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetCandidatesAsync(org, camp.Id, null, null, null, null, "muc tieu", null, 2, default);

        Assert.Equal("Muc Tieu", Assert.Single(page.Items).FullName);
    }

    // ── skill: lọc SAU phân trang (jsonb) ───────────────────────────────────────────────

    // Hợp đồng đã ghi rõ trong XML doc + controller: trang có thể NGẮN HƠN limit (kể cả rỗng) mà VẪN
    // còn trang sau → client phải đi theo X-Next-Cursor tới khi vắng. Khoá lại kẻo ai đó "tối ưu"
    // bằng cách dừng sớm khi thấy trang ngắn và làm mất ứng viên.
    [Fact]
    public async Task Skill_LocSauPhanTrang_TrangNganNhungVanConCursor()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        Seed(tdb.Db, camp.Id, "co-sql", "a@x.com", 90, skills: new List<string> { "SQL", "C#" });
        Seed(tdb.Db, camp.Id, "khong-sql", "b@x.com", 80, skills: new List<string> { "Figma" });
        Seed(tdb.Db, camp.Id, "co-sql-2", "c@x.com", 70, skills: new List<string> { "sql" });
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());

        // Trang 1 (limit 2) đọc 2 dòng từ DB, skill loại 1 → chỉ còn 1 item, NHƯNG vẫn còn trang sau.
        var p1 = await svc.GetCandidatesAsync(org, camp.Id, null, null, "sql", null, null, null, 2, default);
        Assert.Equal("co-sql", Assert.Single(p1.Items).FullName);
        Assert.NotNull(p1.NextCursor);   // ← trang ngắn KHÔNG có nghĩa là hết

        var p2 = await svc.GetCandidatesAsync(org, camp.Id, null, null, "sql", null, null, p1.NextCursor, 2, default);
        Assert.Equal("co-sql-2", Assert.Single(p2.Items).FullName);
        Assert.Null(p2.NextCursor);
    }

    // ── cursor / limit ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CursorRac_VeTrangDau_KhongNo()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        Seed(tdb.Db, camp.Id, "x", "x@x.com", 50);
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext())
            .GetCandidatesAsync(org, camp.Id, null, null, null, null, null, "khong-phai-base64!!", null, default);

        Assert.Single(page.Items);   // cursor hỏng = trang đầu (decode total), không 500
    }

    [Fact]
    public async Task Limit_BiKepTran()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        Seed(tdb.Db, camp.Id, "x", "x@x.com", 50);
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetCandidatesAsync(
            org, camp.Id, null, null, null, null, null, null, KeysetPaging.MaxLimit + 10_000, default);

        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    // Phân trang KHÔNG được làm rơi mất org-scope (ngoài org → 404, không phải trang rỗng).
    [Fact]
    public async Task NgoaiOrg_Nem404()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = SeedCampaign(tdb, org);
        Seed(tdb.Db, camp.Id, "x", "x@x.com", 50);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetCandidatesAsync(
            Guid.NewGuid(), camp.Id, null, null, null, null, null, null, null, default));
    }
}
