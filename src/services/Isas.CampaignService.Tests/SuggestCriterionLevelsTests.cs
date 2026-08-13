using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-16 — <c>POST /campaign/{id}/criteria/levels/suggest</c>: AI soạn mốc, HR xem/sửa.
///
/// <para>Endpoint CHỈ ĐỌC. Không ghi DB nghĩa là validate CAMP-17, audit và luật bump version chỉ tồn
/// tại ở một cửa (<c>PUT /campaign/{id}</c>) thay vì hai bản dễ lệch nhau.</para>
/// </summary>
public class SuggestCriterionLevelsTests
{
    private static CampaignSvc NewService(CampaignDbContext db, IAiServiceLevelSuggester? suggester) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>(),
            levelSuggester: suggester);

    private static CampaignCriterion Criterion(Guid campaignId, int order, string name, int maxScore = 5)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = order, Name = name,
            Weight = 1.0m, MaxScore = maxScore, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    private static async Task<(Campaign Camp, List<CampaignCriterion> Criteria)> SeedAsync(
        CampaignTestDb tdb, Guid owner, CampaignStatus status, int criterionCount = 2)
    {
        var camp = CampaignTestDb.NewCampaign(owner, status);
        camp.Domain = "BE";
        tdb.Db.Campaigns.Add(camp);
        var criteria = Enumerable.Range(0, criterionCount)
            .Select(i => Criterion(camp.Id, i, $"Tiêu chí {i}", maxScore: 5 + i)).ToList();
        tdb.Db.CampaignCriteria.AddRange(criteria);
        await tdb.Db.SaveChangesAsync();
        return (camp, criteria);
    }

    [Fact]
    public async Task Tra_moc_ghep_theo_ID_tieu_chi_kem_ten_va_thang_diem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, criteria) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var ai = new Mock<IAiServiceLevelSuggester>();
        ai.Setup(x => x.SuggestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<LevelSuggestionInput>>(), It.IsAny<CancellationToken>()))
            // CỐ Ý trả ĐẢO thứ tự so với thứ tự gửi đi.
            .ReturnsAsync(new List<SuggestedLevelSet>
            {
                new(criteria[1].Id, new List<SuggestedLevel> { new(6, "mức cao của tiêu chí 1"), new(0, "mức 0 của tiêu chí 1") }),
                new(criteria[0].Id, new List<SuggestedLevel> { new(0, "mức 0 của tiêu chí 0"), new(5, "mức cao của tiêu chí 0") }),
            });

        var res = await NewService(tdb.NewContext(), ai.Object)
            .SuggestCriterionLevelsAsync(owner, camp.Id, default);

        Assert.Equal(2, res.Criteria.Count);
        Assert.Equal(criteria[0].Id, res.Criteria[0].CriterionId);
        Assert.Equal("Tiêu chí 0", res.Criteria[0].Name);
        Assert.Equal(5, res.Criteria[0].MaxScore);
        // Ghép theo ID, không theo thứ tự mảng AI trả — gán nhầm tiêu chí thì HR không có cách nào nhận ra.
        Assert.Contains("tiêu chí 0", res.Criteria[0].Levels[0].Descriptor);
        Assert.Contains("tiêu chí 1", res.Criteria[1].Levels[0].Descriptor);
        // Mốc sắp tăng dần bất kể AI trả thứ tự nào.
        Assert.Equal(new[] { 0, 5 }, res.Criteria[0].Levels.Select(l => l.Score));
        Assert.Equal(new[] { 0, 6 }, res.Criteria[1].Levels.Select(l => l.Score));
    }

    // AI bỏ sót một tiêu chí → tiêu chí đó về mảng rỗng, KHÔNG kéo mốc của tiêu chí khác sang.
    [Fact]
    public async Task AI_bo_sot_tieu_chi_thi_tra_rong_cho_dung_tieu_chi_do()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, criteria) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var ai = new Mock<IAiServiceLevelSuggester>();
        ai.Setup(x => x.SuggestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<LevelSuggestionInput>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SuggestedLevelSet>
            {
                new(criteria[0].Id, new List<SuggestedLevel> { new(0, "a"), new(5, "b") }),
            });

        var res = await NewService(tdb.NewContext(), ai.Object)
            .SuggestCriterionLevelsAsync(owner, camp.Id, default);

        Assert.Equal(2, res.Criteria[0].Levels.Count);
        Assert.Empty(res.Criteria[1].Levels);
    }

    // Campaign Active vẫn gợi ý được — HR sửa mốc trên chiến dịch đang chạy là hành vi được thiết kế.
    [Fact]
    public async Task Campaign_Active_van_goi_y_duoc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, criteria) = await SeedAsync(tdb, owner, CampaignStatus.Active);

        var ai = new Mock<IAiServiceLevelSuggester>();
        ai.Setup(x => x.SuggestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<LevelSuggestionInput>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(criteria.Select(c => new SuggestedLevelSet(
                c.Id, new List<SuggestedLevel> { new(0, "a"), new(c.MaxScore, "b") })).ToList());

        var res = await NewService(tdb.NewContext(), ai.Object)
            .SuggestCriterionLevelsAsync(owner, camp.Id, default);
        Assert.Equal(2, res.Criteria.Count);
    }

    [Theory]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Campaign_da_dong_thi_409(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, status);

        var ai = new Mock<IAiServiceLevelSuggester>(MockBehavior.Strict);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext(), ai.Object).SuggestCriterionLevelsAsync(owner, camp.Id, default));
        ai.VerifyNoOtherCalls();   // không đốt token cho một request đằng nào cũng bị từ chối
    }

    [Fact]
    public async Task Chua_co_tieu_chi_thi_400_va_khong_goi_AI()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Draft, criterionCount: 0);

        var ai = new Mock<IAiServiceLevelSuggester>(MockBehavior.Strict);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext(), ai.Object).SuggestCriterionLevelsAsync(owner, camp.Id, default));
        ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Campaign_ngoai_org_thi_404()
    {
        using var tdb = new CampaignTestDb();
        var (camp, _) = await SeedAsync(tdb, Guid.NewGuid(), CampaignStatus.Draft);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext(), Mock.Of<IAiServiceLevelSuggester>())
                .SuggestCriterionLevelsAsync(Guid.NewGuid(), camp.Id, default));
    }

    // AI hỏng → 502 và KHÔNG ghi dòng nào. Fallback dải mặc định ở đây nghĩa là HR tin "Mức 3/10" do
    // AI soạn rồi publish một thước đo chưa ai viết.
    [Fact]
    public async Task AI_loi_thi_502_va_khong_ghi_dong_nao()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var ai = new Mock<IAiServiceLevelSuggester>();
        ai.Setup(x => x.SuggestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<LevelSuggestionInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamServiceException("AIService sập"));

        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewService(tdb.NewContext(), ai.Object).SuggestCriterionLevelsAsync(owner, camp.Id, default));

        using var check = tdb.NewContext();
        Assert.Empty(await check.CampaignCriterionLevels.ToListAsync());
    }

    // Endpoint là CHỈ ĐỌC — kể cả lượt thành công cũng không được chạm DB.
    [Fact]
    public async Task Luot_thanh_cong_cung_KHONG_ghi_DB()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, criteria) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var ai = new Mock<IAiServiceLevelSuggester>();
        ai.Setup(x => x.SuggestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<LevelSuggestionInput>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(criteria.Select(c => new SuggestedLevelSet(
                c.Id, new List<SuggestedLevel> { new(0, "a"), new(c.MaxScore, "b") })).ToList());

        await NewService(tdb.NewContext(), ai.Object).SuggestCriterionLevelsAsync(owner, camp.Id, default);

        using var check = tdb.NewContext();
        Assert.Empty(await check.CampaignCriterionLevels.ToListAsync());
    }

    // Ngữ cảnh gửi đi phải là ngữ cảnh THẬT của chiến dịch — mốc soạn theo ngôn ngữ/cấp độ khác sẽ
    // lệch hẳn với bộ chấm (bộ chấm đọc campaign.Language/Seniority).
    [Fact]
    public async Task Gui_dung_ngu_canh_campaign_cho_AI()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, criteria) = await SeedAsync(tdb, owner, CampaignStatus.Draft);
        camp.Language = "en";
        camp.Seniority = "Senior";
        camp.JDText = "JD nội dung";
        tdb.Db.Campaigns.Update(camp);
        await tdb.Db.SaveChangesAsync();

        string? lang = null, seniority = null, jd = null, job = null;
        IReadOnlyList<LevelSuggestionInput>? sent = null;
        var ai = new Mock<IAiServiceLevelSuggester>();
        ai.Setup(x => x.SuggestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<LevelSuggestionInput>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, IReadOnlyList<LevelSuggestionInput>, CancellationToken>(
                (j, l, s, d, c, _) => { job = j; lang = l; seniority = s; jd = d; sent = c; })
            .ReturnsAsync(new List<SuggestedLevelSet>());

        await NewService(tdb.NewContext(), ai.Object).SuggestCriterionLevelsAsync(owner, camp.Id, default);

        Assert.Equal("BE", job);
        Assert.Equal("en", lang);
        Assert.Equal("Senior", seniority);
        Assert.Equal("JD nội dung", jd);
        Assert.Equal(new[] { 5, 6 }, sent!.Select(c => c.MaxScore));   // thang điểm THẬT của từng tiêu chí
    }
}
