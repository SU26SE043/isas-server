using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// HR technical screener — bước 1: chốt bộ NHU CẦU CÔNG VIỆC cho campaign.
///
/// Vì sao bước này phải chạy MỘT LẦN cho cả campaign chứ không suy lại theo từng CV: nó chỉ đọc
/// JD, không đọc CV, nên nó là thuộc tính của vị trí tuyển dụng. Suy lại mỗi hồ sơ thì không gì
/// buộc hai lần đọc ra cùng bộ nhu cầu ⇒ hai ứng viên cùng campaign bị đo bằng hai cái thước khác
/// nhau rồi xếp chung một bảng — đúng thứ bất công CAMP-10 chặn ở đường phỏng vấn.
///
/// "AI đề xuất, HR chốt" (mẫu D13/SEC-4): AI điền sẵn lúc publish, HR sửa được khi còn Draft.
/// </summary>
public class JobNeedsScreeningTests
{
    private static CampaignSvc NewService(
        CampaignDbContext db, IJobNeedsSuggester? suggester = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            entitlements: Entitlements(), jobNeedsSuggester: suggester);

    private static IEntitlementClient Entitlements()
    {
        var client = new Mock<IEntitlementClient>();
        client.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            new CampaignEntitlement("test", "business", 1, 10, 200, true, true, true));
        return client.Object;
    }

    private static Campaign SeedDraft(CampaignTestDb tdb, Guid owner, string? jd = "JD: cần Backend .NET")
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        camp.Domain = "BE";
        camp.JDText = jd;
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            QuestionText = "Câu hỏi 1",
            Source = QuestionSource.CustomHr,
            IsRequired = true,
            CreatedAt = DateTime.UtcNow,
        });
        tdb.Db.SaveChanges();
        return camp;
    }

    private static IJobNeedsSuggester Suggester(params SuggestedJobNeed[] needs)
    {
        var m = new Mock<IJobNeedsSuggester>();
        m.Setup(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                                    It.IsAny<CancellationToken>()))
         .ReturnsAsync(needs.ToList());
        return m.Object;
    }

    // ── Publish: AI đề xuất → lưu vào campaign, gắn nguồn AiSuggested ─────────────────────

    [Fact]
    public async Task Publish_goi_AI_va_luu_job_needs()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);

        var svc = NewService(tdb.NewContext(), Suggester(
            new SuggestedJobNeed(JobNeedCategories.Technical, "Thạo .NET"),
            new SuggestedJobNeed(JobNeedCategories.Communication, "Làm việc với khách Nhật")));
        await svc.PublishCampaignAsync(owner, owner, camp.Id, default);

        using var check = tdb.NewContext();
        var saved = await check.Campaigns.FirstAsync(c => c.Id == camp.Id);
        Assert.Equal(2, saved.JobNeeds!.Count);
        Assert.Equal("Thạo .NET", saved.JobNeeds[0].Text);
        Assert.All(saved.JobNeeds, n => Assert.Equal(JobNeedSources.AiSuggested, n.Source));
        Assert.All(saved.JobNeeds, n => Assert.False(string.IsNullOrWhiteSpace(n.NeedId)));
    }

    // 🔴 HR đã chốt rồi thì publish KHÔNG được đè lên. Bộ nhu cầu là quyết định của người —
    // đúng nguyên tắc "AI gợi ý, người quyết" mà D13 đặt ra cho cờ chống gian lận.
    [Fact]
    public async Task Publish_KHONG_de_len_bo_HR_da_chot()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);
        camp.JobNeeds = new List<JobNeed>
        {
            new() { NeedId = "hr-1", Category = JobNeedCategories.Technical,
                    Text = "HR tự khai", Source = JobNeedSources.HrEdited },
        };
        tdb.Db.SaveChanges();

        var svc = NewService(tdb.NewContext(), Suggester(
            new SuggestedJobNeed(JobNeedCategories.Technical, "AI muốn ghi đè")));
        await svc.PublishCampaignAsync(owner, owner, camp.Id, default);

        using var check = tdb.NewContext();
        var saved = await check.Campaigns.FirstAsync(c => c.Id == camp.Id);
        Assert.Equal("HR tự khai", Assert.Single(saved.JobNeeds!).Text);
    }

    // AI chết ⇒ giữ nguyên thứ đang có, KHÔNG xoá. Publish vẫn đi tiếp (campaign không bị kẹt vì
    // AIService down); sàng CV sẽ dừng với lý do đọc được — xem `Chua_chot_job_needs_thi_khong_sang_duoc`.
    [Fact]
    public async Task Publish_khi_AI_loi_thi_khong_xoa_job_needs()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);

        var dead = new Mock<IJobNeedsSuggester>();
        dead.Setup(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                                       It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<SuggestedJobNeed>?)null);

        var svc = NewService(tdb.NewContext(), dead.Object);
        var res = await svc.PublishCampaignAsync(owner, owner, camp.Id, default);

        Assert.Equal(nameof(CampaignStatus.Active), res.Status);
        using var check = tdb.NewContext();
        Assert.Null((await check.Campaigns.FirstAsync(c => c.Id == camp.Id)).JobNeeds);
    }

    // ── HR sửa (replace-all) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task HR_sua_job_needs_thi_luu_va_gan_nguon_HrEdited()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);

        var svc = NewService(tdb.NewContext());
        var res = await svc.ReplaceJobNeedsAsync(owner, owner, camp.Id, new List<JobNeedInput>
        {
            new() { Category = JobNeedCategories.Technical, Text = "  Thạo Kafka  " },
            new() { Category = JobNeedCategories.Growth, Text = "Học nhanh" },
            new() { Category = JobNeedCategories.Technical, Text = "   " },   // rỗng → bỏ
        }, default);

        Assert.Equal(2, res.JobNeeds.Count);
        Assert.Equal("Thạo Kafka", res.JobNeeds[0].Text);   // trim
        Assert.All(res.JobNeeds, n => Assert.Equal(JobNeedSources.HrEdited, n.Source));
    }

    // 🔴 Nguồn gốc là sự thật do SERVER sở hữu. Cho client khai `source` thì HR tự dán nhãn
    // "AI đề xuất" cho dòng mình gõ tay — đúng lỗ F10 đã bịt cho `campaign_questions.source`.
    // Khoá ở tầng DTO: `JobNeedInput` KHÔNG được có property nào tên `Source`.
    [Fact]
    public void Input_dto_khong_duoc_co_field_source()
    {
        Assert.Null(typeof(JobNeedInput).GetProperty("Source"));
    }

    // Echo lại id đang có ⇒ giữ nguyên, để kết quả sàng đã lưu còn trỏ đúng dòng (mẫu F10 giữ id
    // câu hỏi qua vòng đọc→sửa→lưu). Id trống ⇒ cấp mới.
    [Fact]
    public async Task HR_echo_lai_needId_thi_giu_nguyen_id()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);

        var svc = NewService(tdb.NewContext());
        var res = await svc.ReplaceJobNeedsAsync(owner, owner, camp.Id, new List<JobNeedInput>
        {
            new() { NeedId = "need-cu", Category = JobNeedCategories.Technical, Text = "sửa chữ thôi" },
            new() { Category = JobNeedCategories.Growth, Text = "dòng mới" },
        }, default);

        Assert.Equal("need-cu", res.JobNeeds[0].NeedId);
        Assert.False(string.IsNullOrWhiteSpace(res.JobNeeds[1].NeedId));
        Assert.NotEqual("need-cu", res.JobNeeds[1].NeedId);
    }

    // CAMP-2: đổi thước đo giữa chừng làm ứng viên sàng trước và sàng sau không so sánh được nữa.
    [Theory]
    [InlineData(CampaignStatus.Active)]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Sua_job_needs_khi_khong_con_Draft_thi_409(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);
        camp.Status = status;
        tdb.Db.SaveChanges();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ReplaceJobNeedsAsync(owner, owner, camp.Id, new List<JobNeedInput>
            {
                new() { Category = JobNeedCategories.Technical, Text = "x" },
            }, default));
    }

    // Nhóm lạ bị chặn tại cửa: `job_needs` đi thẳng vào prompt sàng CV và vào màn HR, nên giá trị
    // ngoài tập đã biết là rác câm.
    [Fact]
    public async Task Nhom_nhu_cau_la_thi_400()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.ReplaceJobNeedsAsync(owner, owner, camp.Id, new List<JobNeedInput>
            {
                new() { Category = "Vibes", Text = "x" },
            }, default));
    }

    [Fact]
    public async Task Campaign_ngoai_org_thi_404()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.ReplaceJobNeedsAsync(Guid.NewGuid(), owner, camp.Id, new List<JobNeedInput>
            {
                new() { Category = JobNeedCategories.Technical, Text = "x" },
            }, default));
    }

    // ── Công thức điểm — khoá bằng chính bảng mức, không qua DB ───────────────────────────
    //
    // Strong=1 · Partial=0.5 · Weak=0 · mức lạ=0. Mức lạ KHÔNG được ăn nửa điểm: mặc định an toàn
    // là "chưa chứng minh được", vì mọi hướng khác đều cho không ứng viên một phần điểm mà không
    // ai đọc được bằng chứng nào.
    [Theory]
    [InlineData(NeedLevels.Strong, 1.0)]
    [InlineData(NeedLevels.Partial, 0.5)]
    [InlineData(NeedLevels.Weak, 0.0)]
    [InlineData("Xuất sắc", 0.0)]
    [InlineData(null, 0.0)]
    public void Bang_muc_diem_dung_nhu_thiet_ke(string? level, double expected)
    {
        Assert.Equal((decimal)expected, NeedLevels.Credit(level));
    }
}
