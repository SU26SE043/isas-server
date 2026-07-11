using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// C12 — Tiêu chí chấm CÓ CẤU TRÚC, HR khai thẳng (criteria[] = {name,description?,weight,maxScore}).
/// (a) criteria[] hợp lệ → replace-all campaign_criteria (HrEdited), Σweight=1, order_no đúng;
/// (b) Σweight ngoài [0.99,1.01] → 400 (ArgumentException);
/// (c) name trùng / weight / maxScore sai → 400;
/// (d) sửa criteria khi Active → 409 (InvalidOperationException);
/// (e) publish với criteria[] có sẵn → KHÔNG gọi AI, giữ nguyên criteria.
/// (controller map ArgumentException→400, InvalidOperationException→409.)
/// </summary>
public class CampaignStructuredCriteriaTests
{
    private static CampaignSvc NewService(CampaignDbContext db, ICriteriaSuggester? suggester = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(),
            suggester ?? Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static CreateCampaignRequest NewCreateReq(List<CriterionItem>? criteria) => new()
    {
        Title = "Tuyển BE",
        Domain = "BE",
        TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddDays(1),
        ExpiresAt = DateTime.UtcNow.AddDays(10),
        Criteria = criteria,
        // Không câu hỏi — cô lập hành vi criteria của create (question Id dùng default DB không có trên SQLite;
        // publish mới đòi ≥1 câu — xem test (e)).
        Questions = new List<QuestionItem>()
    };

    private static List<CriterionItem> ValidCriteria() => new()
    {
        new() { Name = "Chuyên môn",        Weight = 0.5m, MaxScore = 5,  Description = "Kiến thức" },
        new() { Name = "Giao tiếp",         Weight = 0.3m, MaxScore = 5 },
        new() { Name = "Giải quyết vấn đề", Weight = 0.2m, MaxScore = 10 },
    };

    private static CampaignCriterion SeedCriterion(Guid campaignId, int order, string name, decimal weight, CriterionSource source)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = order, Name = name,
            Weight = weight, MaxScore = 5, Source = source,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    // (a) Create với criteria[] hợp lệ → campaign_criteria (HrEdited), Σweight=1, order_no 0..n theo thứ tự gửi.
    [Fact]
    public async Task Create_voi_criteria_hop_le_luu_HrEdited_order_no_va_sum_weight_1()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var svc = NewService(tdb.NewContext());

        var res = await svc.CreateCampaignAsync(owner, NewCreateReq(ValidCriteria()), default);

        Assert.Equal(3, res.Criteria.Count);   // response phản ánh tiêu chí

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria
            .Where(c => c.CampaignId == res.Id).OrderBy(c => c.OrderNo).ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 0, 1, 2 }, rows.Select(r => r.OrderNo).ToArray());
        Assert.Equal(new[] { "Chuyên môn", "Giao tiếp", "Giải quyết vấn đề" }, rows.Select(r => r.Name).ToArray());
        Assert.All(rows, r => Assert.Equal(CriterionSource.HrEdited, r.Source));
        Assert.Equal(1.0m, rows.Sum(r => r.Weight));                                   // Σweight = 1
        Assert.Equal(10, rows.Single(r => r.Name == "Giải quyết vấn đề").MaxScore);    // maxScore giữ nguyên
    }

    // (a) Σweight biên trong khoảng (0.99) → CHUẨN HOÁ Σ→1 (không 400).
    [Fact]
    public async Task Create_criteria_sum_099_duoc_chuan_hoa_ve_1()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var svc = NewService(tdb.NewContext());

        var criteria = new List<CriterionItem>
        {
            new() { Name = "A", Weight = 0.33m, MaxScore = 5 },
            new() { Name = "B", Weight = 0.33m, MaxScore = 5 },
            new() { Name = "C", Weight = 0.33m, MaxScore = 5 },   // Σ=0.99 (biên trong) → chuẩn hoá
        };
        var res = await svc.CreateCampaignAsync(owner, NewCreateReq(criteria), default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == res.Id).ToListAsync();
        Assert.Equal(1.0m, rows.Sum(r => r.Weight));
    }

    // (a) Update criteria[] = REPLACE-ALL atomic: xoá bộ cũ (kể cả order_no trùng), ghi bộ mới HrEdited, Σ=1.
    [Fact]
    public async Task Update_criteria_replace_all_xoa_bo_cu_ghi_bo_moi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(camp);
        // bộ cũ (AiSuggested), order_no 0/1 sẽ TRÙNG với bộ mới → chứng minh replace-all không vỡ UNIQUE.
        tdb.Db.CampaignCriteria.AddRange(
            SeedCriterion(camp.Id, 0, "Old1", 0.6m, CriterionSource.AiSuggested),
            SeedCriterion(camp.Id, 1, "Old2", 0.4m, CriterionSource.AiSuggested));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var req = new UpdateCampaignRequest
        {
            Title = "Tuyển BE",
            Criteria = new List<CriterionItem>
            {
                new() { Name = "New A", Weight = 0.5m, MaxScore = 5 },
                new() { Name = "New B", Weight = 0.5m, MaxScore = 5 },
            }
        };
        await svc.UpdateCampaignAsync(owner, camp.Id, req, default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria
            .Where(c => c.CampaignId == camp.Id).OrderBy(c => c.OrderNo).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "New A", "New B" }, rows.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { 0, 1 }, rows.Select(r => r.OrderNo).ToArray());
        Assert.All(rows, r => Assert.Equal(CriterionSource.HrEdited, r.Source));
        Assert.DoesNotContain(rows, r => r.Name is "Old1" or "Old2");   // bộ cũ đã xoá
        Assert.Equal(1.0m, rows.Sum(r => r.Weight));
    }

    // (b) Σweight ngoài [0.99,1.01] → ArgumentException (→400).
    [Theory]
    [InlineData(0.5, 0.3)]   // Σ=0.8 < 0.99
    [InlineData(0.8, 0.4)]   // Σ=1.2 > 1.01
    public async Task Create_criteria_sum_weight_ngoai_khoang_nem_ArgumentException(double w1, double w2)
    {
        using var tdb = new CampaignTestDb();
        var svc = NewService(tdb.NewContext());
        var criteria = new List<CriterionItem>
        {
            new() { Name = "A", Weight = (decimal)w1, MaxScore = 5 },
            new() { Name = "B", Weight = (decimal)w2, MaxScore = 5 },
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateCampaignAsync(Guid.NewGuid(), NewCreateReq(criteria), default));
    }

    // (c) name trùng (case-insensitive) → ArgumentException.
    [Fact]
    public async Task Create_criteria_ten_trung_nem_ArgumentException()
    {
        using var tdb = new CampaignTestDb();
        var svc = NewService(tdb.NewContext());
        var criteria = new List<CriterionItem>
        {
            new() { Name = "Giao tiếp", Weight = 0.5m, MaxScore = 5 },
            new() { Name = "giao tiếp", Weight = 0.5m, MaxScore = 5 },   // trùng (case-insensitive)
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateCampaignAsync(Guid.NewGuid(), NewCreateReq(criteria), default));
    }

    // (c) name rỗng/whitespace → ArgumentException.
    [Fact]
    public async Task Create_criteria_ten_rong_nem_ArgumentException()
    {
        using var tdb = new CampaignTestDb();
        var svc = NewService(tdb.NewContext());
        var criteria = new List<CriterionItem>
        {
            new() { Name = "   ", Weight = 0.5m, MaxScore = 5 },
            new() { Name = "B",   Weight = 0.5m, MaxScore = 5 },
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateCampaignAsync(Guid.NewGuid(), NewCreateReq(criteria), default));
    }

    // (c) weight ngoài (0,1] → ArgumentException.
    [Theory]
    [InlineData(0)]      // ≤ 0
    [InlineData(-0.1)]   // < 0
    [InlineData(1.5)]    // > 1
    public async Task Create_criteria_weight_sai_nem_ArgumentException(double badWeight)
    {
        using var tdb = new CampaignTestDb();
        var svc = NewService(tdb.NewContext());
        var criteria = new List<CriterionItem>
        {
            new() { Name = "A", Weight = (decimal)badWeight, MaxScore = 5 },
            new() { Name = "B", Weight = 0.5m,              MaxScore = 5 },
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateCampaignAsync(Guid.NewGuid(), NewCreateReq(criteria), default));
    }

    // (c) maxScore < 1 → ArgumentException.
    [Fact]
    public async Task Create_criteria_maxScore_nho_hon_1_nem_ArgumentException()
    {
        using var tdb = new CampaignTestDb();
        var svc = NewService(tdb.NewContext());
        var criteria = new List<CriterionItem>
        {
            new() { Name = "A", Weight = 0.5m, MaxScore = 0 },   // < 1
            new() { Name = "B", Weight = 0.5m, MaxScore = 5 },
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateCampaignAsync(Guid.NewGuid(), NewCreateReq(criteria), default));
    }

    // (d) sửa criteria khi campaign Active → InvalidOperationException (→409). KHÔNG đụng bộ cũ.
    [Fact]
    public async Task Update_criteria_khi_Active_nem_InvalidOperationException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignCriteria.Add(SeedCriterion(camp.Id, 0, "Keep", 1.0m, CriterionSource.AiSuggested));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var req = new UpdateCampaignRequest
        {
            Title = "Tuyển BE",
            Criteria = new List<CriterionItem> { new() { Name = "New", Weight = 1.0m, MaxScore = 5 } }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateCampaignAsync(owner, camp.Id, req, default));

        // bộ cũ giữ nguyên (không nửa vời)
        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Keep", rows[0].Name);
    }

    // (e) publish với criteria[] HrEdited có sẵn → KHÔNG gọi AI, giữ nguyên tiêu chí.
    [Fact]
    public async Task Publish_voi_criteria_HrEdited_co_san_khong_goi_AI_giu_nguyen()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignCriteria.AddRange(
            SeedCriterion(camp.Id, 0, "HR-1", 0.7m, CriterionSource.HrEdited),
            SeedCriterion(camp.Id, 1, "HR-2", 0.3m, CriterionSource.HrEdited));
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, EmployerId = owner,
            QuestionText = "Q1", Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var suggester = new Mock<ICriteriaSuggester>();
        var svc = NewService(tdb.NewContext(), suggester.Object);

        var res = await svc.PublishCampaignAsync(owner, camp.Id, default);
        Assert.Equal("Active", res.Status);

        // đã có criteria[] → publish KHÔNG gọi AIService
        suggester.Verify(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria
            .Where(c => c.CampaignId == camp.Id).OrderBy(c => c.OrderNo).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "HR-1", "HR-2" }, rows.Select(r => r.Name).ToArray());
        Assert.All(rows, r => Assert.Equal(CriterionSource.HrEdited, r.Source));   // giữ nguyên
    }
}
