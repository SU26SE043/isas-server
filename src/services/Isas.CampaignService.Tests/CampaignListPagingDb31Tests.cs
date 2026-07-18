using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Pagination;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// DB31 — list campaign của Employer (endpoint user thật sự gọi) nay keyset-paged, theo ĐÚNG
/// convention DB8 của `ListAllCampaignsAsync`: cursor opaque `(CreatedAt DESC, Id DESC)`, limit mặc
/// định = cap cũ (500 → hành vi cũ giữ nguyên), body vẫn mảng, next-cursor rời ra header.
/// Vẫn phải giữ nguyên: lọc theo org + soft-delete (D11) + 2 Include (card hiện số câu hỏi/tiêu chí).
/// </summary>
public class CampaignListPagingDb31Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static Campaign SeedAt(CampaignDbContext db, Guid orgId, string title, DateTime createdAt)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        c.Title = title;
        c.CreatedAt = createdAt;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    [Fact]
    public async Task List_KhongCursor_TraTrangDau_MoiNhatTruoc()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var t0 = DateTime.UtcNow;
        SeedAt(tdb.Db, org, "cu", t0.AddHours(-3));
        SeedAt(tdb.Db, org, "giua", t0.AddHours(-2));
        SeedAt(tdb.Db, org, "moi", t0.AddHours(-1));

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(org, null, null, default);

        Assert.Equal(new[] { "moi", "giua", "cu" }, page.Items.Select(c => c.Title));
        Assert.Null(page.NextCursor);   // < limit → hết trang, không phát cursor (backward-compat)
    }

    [Fact]
    public async Task List_PhanTrang_Bang_Cursor_KhongTrungKhongSot()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            SeedAt(tdb.Db, org, $"c{i}", t0.AddHours(-i));   // c0 mới nhất

        var svc = NewService(tdb.NewContext());

        var p1 = await svc.GetCampaignsAsync(org, null, 2, default);
        Assert.Equal(new[] { "c0", "c1" }, p1.Items.Select(c => c.Title));
        Assert.NotNull(p1.NextCursor);

        var p2 = await svc.GetCampaignsAsync(org, p1.NextCursor, 2, default);
        Assert.Equal(new[] { "c2", "c3" }, p2.Items.Select(c => c.Title));

        var p3 = await svc.GetCampaignsAsync(org, p2.NextCursor, 2, default);
        Assert.Equal(new[] { "c4" }, p3.Items.Select(c => c.Title));
        Assert.Null(p3.NextCursor);   // trang cuối chưa đầy → hết
    }

    // Trùng created_at (import hàng loạt / cùng transaction) là ca duy nhất khiến keyset trượt
    // nếu thiếu tie-break theo Id — phải đủ 3 row, không lặp, không mất.
    [Fact]
    public async Task List_TrungCreatedAt_TieBreakTheoId_KhongLapKhongMat()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var same = DateTime.UtcNow.AddMinutes(-10);
        SeedAt(tdb.Db, org, "a", same);
        SeedAt(tdb.Db, org, "b", same);
        SeedAt(tdb.Db, org, "c", same);

        var svc = NewService(tdb.NewContext());
        var seen = new List<string>();
        string? cursor = null;

        for (var guard = 0; guard < 5; guard++)
        {
            var page = await svc.GetCampaignsAsync(org, cursor, 1, default);
            seen.AddRange(page.Items.Select(c => c.Title!));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(new[] { "a", "b", "c" }, seen.OrderBy(x => x));
    }

    [Fact]
    public async Task List_CursorRac_VeTrangDau_KhongNo()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        SeedAt(tdb.Db, org, "x", DateTime.UtcNow);

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(org, "khong-phai-base64!!", null, default);

        Assert.Single(page.Items);   // cursor hỏng = trang đầu (KeysetCursor.Decode total), không 500
    }

    [Fact]
    public async Task List_Limit_BiKepTran_KhongCho_Keo_Vo_Han()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        SeedAt(tdb.Db, org, "x", DateTime.UtcNow);

        // limit vượt trần → kẹp về MaxLimit (không cho client tự nâng cỡ trang tuỳ ý).
        var page = await NewService(tdb.NewContext())
            .GetCampaignsAsync(org, null, KeysetPaging.MaxLimit + 10_000, default);

        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    // Phân trang KHÔNG được làm rơi mất org-scope / soft-delete / Include — 3 thứ list này vốn có.
    [Fact]
    public async Task List_GiuNguyen_OrgScope_SoftDelete_VaInclude()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var orgKhac = Guid.NewGuid();

        var mine = SeedAt(tdb.Db, org, "cua-toi", DateTime.UtcNow);
        var deleted = SeedAt(tdb.Db, org, "da-xoa", DateTime.UtcNow.AddHours(-1));
        deleted.DeletedAt = DateTime.UtcNow;
        SeedAt(tdb.Db, orgKhac, "cua-org-khac", DateTime.UtcNow);

        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(),
            CampaignId = mine.Id,
            QuestionText = "Câu hỏi?",
            CreatedAt = DateTime.UtcNow
        });
        tdb.Db.CampaignCriteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(),
            CampaignId = mine.Id,
            Name = "Tiêu chí",
            Weight = 1m,
            MaxScore = 10,
            OrderNo = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(org, null, null, default);

        var only = Assert.Single(page.Items);
        Assert.Equal("cua-toi", only.Title);                    // org khác không lọt, soft-delete bị loại
        Assert.Single(only.Questions);                          // Include Questions còn sống qua AsSplitQuery
        Assert.Single(only.Criteria);                           // Include Criteria còn sống (C12 — card đếm tiêu chí)
    }
}
