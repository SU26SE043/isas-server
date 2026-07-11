using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

public class CampaignServiceTests
{
    private static CampaignSvc NewService(CampaignDbContext db, ICriteriaSuggester? suggester = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            suggester ?? Mock.Of<ICriteriaSuggester>(),    // mock mặc định → null → fallback default criteria
            Mock.Of<IInvitationEmailPublisher>());         // D1: không dùng ở test file này

    // C2 + ownership: GetCampaigns chỉ trả campaign của chính employer (không rò rỉ của người khác)
    [Fact]
    public async Task GetCampaigns_chi_tra_cua_chinh_employer()
    {
        using var tdb = new CampaignTestDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        tdb.Db.Campaigns.AddRange(
            CampaignTestDb.NewCampaign(a),
            CampaignTestDb.NewCampaign(b),
            CampaignTestDb.NewCampaign(a));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var list = await svc.GetCampaignsAsync(a, default);

        Assert.Equal(2, list.Count);
        Assert.All(list, c => Assert.Equal(a, c.EmployerId));
    }

    // ownership: lấy campaign của employer khác → KeyNotFound (404), không lộ tồn tại
    [Fact]
    public async Task GetCampaign_cua_employer_khac_nem_NotFound()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => svc.GetCampaignAsync(Guid.NewGuid(), camp.Id, default));
    }

    // C3: Update với AntiCheatEnabled = null → KHÔNG ghi đè, giữ giá trị cũ
    [Fact]
    public async Task Update_AntiCheat_null_giu_gia_tri_cu()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, antiCheat: true);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var res = await svc.UpdateCampaignAsync(owner, camp.Id,
            new UpdateCampaignRequest { Title = "New", AntiCheatEnabled = null }, default);

        Assert.True(res.AntiCheatEnabled);   // vẫn true (CampaignResponse.AntiCheatEnabled là bool)
        Assert.Equal("New", res.Title);
    }

    // C9: Delete = soft delete — deleted_at set, row vẫn còn, query thường ẩn nó
    [Fact]
    public async Task Delete_la_soft_va_an_khoi_query()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var ok = await svc.DeleteCampaignAsync(owner, camp.Id, default);
        Assert.True(ok);

        // query thường (global filter) → không thấy
        var visible = await svc.GetCampaignsAsync(owner, default);
        Assert.Empty(visible);

        // row vẫn còn (soft) + deleted_at đã set
        using var check = tdb.NewContext();
        var row = await check.Campaigns.IgnoreQueryFilters().FirstAsync(c => c.Id == camp.Id);
        Assert.NotNull(row.DeletedAt);
    }

    // C7: sửa câu hỏi khi campaign != Draft → InvalidOperationException (controller map 409)
    [Fact]
    public async Task UpdateQuestions_khi_khong_Draft_nem_loi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateCampaignQuestionsAsync(owner, camp.Id,
                new List<QuestionItem> { new() { QuestionText = "Q1", IsRequired = true } }, default));
    }

    // C8 + C10: publish → Active + campaign_criteria (Σweight=1) + audit Publish
    [Fact]
    public async Task Publish_tao_criteria_Sum1_va_audit()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, EmployerId = owner,
            QuestionText = "Q1", Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var res = await svc.PublishCampaignAsync(owner, camp.Id, default);
        Assert.Equal("Active", res.Status);

        using var check = tdb.NewContext();
        var criteria = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.NotEmpty(criteria);
        Assert.Equal(1.0m, criteria.Sum(c => c.Weight));   // Σweight = 1
        Assert.All(criteria, c => Assert.Equal(CriterionSource.AiSuggested, c.Source));

        var audit = await check.AuditLogs
            .Where(a => a.EntityId == camp.Id && a.Action == AuditAction.Publish).ToListAsync();
        Assert.Single(audit);
        Assert.Equal(owner, audit[0].ActorUserId);
    }

    // C8 guard: publish khi không Draft → InvalidOperationException
    [Fact]
    public async Task Publish_khi_khong_Draft_nem_loi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PublishCampaignAsync(owner, camp.Id, default));
    }

    // C7: transition hợp lệ Active→Closed ok; bước nhảy sai Active→Archived → throw
    [Fact]
    public async Task Transition_Active_to_Closed_ok_va_buoc_sai_throw()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var res = await svc.TransitionStatusAsync(owner, camp.Id, CampaignStatus.Closed, default);
        Assert.Equal("Closed", res.Status);

        // Active→Archived (nhảy bước) trên campaign khác → throw
        var camp2 = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        using (var c2 = tdb.NewContext()) { c2.Campaigns.Add(camp2); await c2.SaveChangesAsync(); }
        var svc2 = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc2.TransitionStatusAsync(owner, camp2.Id, CampaignStatus.Archived, default));
    }

    // C8: publish dùng tiêu chí AIService trả về (nếu có), chuẩn hoá Σweight=1
    [Fact]
    public async Task Publish_dung_tieu_chi_tu_AI_neu_co()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, EmployerId = owner,
            QuestionText = "Q1", Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var suggester = new Mock<ICriteriaSuggester>();
        suggester.Setup(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SuggestedCriterion>
            {
                new("AI-Tech", "kt", 0.6m, 5),
                new("AI-Comm", "gt", 0.4m, 5),
            });

        var svc = NewService(tdb.NewContext(), suggester.Object);
        await svc.PublishCampaignAsync(owner, camp.Id, default);

        using var check = tdb.NewContext();
        var criteria = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(2, criteria.Count);
        Assert.Contains(criteria, c => c.Name == "AI-Tech");
        Assert.Equal(1.0m, criteria.Sum(c => c.Weight));   // chuẩn hoá Σ = 1
    }
}
