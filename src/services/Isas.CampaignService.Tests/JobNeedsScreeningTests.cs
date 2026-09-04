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
/// "AI đề xuất, HR chốt" (mẫu D13/SEC-4): AI điền sẵn lúc publish. CMP1-B2 — cửa sửa KHÔNG khoá theo
/// Draft/Active, mà theo bất biến "chưa ai được sàng": AI sinh job_needs LÚC PUBLISH (đúng lúc
/// campaign vừa chuyển Active), nên "chỉ sửa khi Draft" sẽ không bao giờ chạm được nội dung AI vừa
/// sinh — đó là bug đã sống trên dev tới trước bản vá này.
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
    // ⚠ `Active` KHÔNG còn ở đây: CMP1-B2 cho sửa khi Active MÀ CHƯA ai được sàng (không đòi
    // job_needs rỗng nữa). Các ca Active nằm ở nhóm test bên dưới.
    [Theory]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Sua_job_needs_khi_Closed_Archived_thi_409(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);
        camp.Status = status;
        tdb.Db.SaveChanges();

        var svc = NewService(tdb.NewContext());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ReplaceJobNeedsAsync(owner, owner, camp.Id, new List<JobNeedInput>
            {
                new() { Category = JobNeedCategories.Technical, Text = "x" },
            }, default));

        Assert.Contains(status.ToString(), ex.Message);   // thông điệp nêu đúng trạng thái đang khoá
    }

    // ── CMP1-B2 — cửa sửa: Draft, HOẶC Active mà CHƯA có người được sàng ───────────────────────

    private static void SetStatus(CampaignTestDb tdb, Guid campaignId, CampaignStatus status,
        List<JobNeed>? jobNeeds = null)
    {
        var camp = tdb.Db.Campaigns.First(c => c.Id == campaignId);
        camp.Status = status;
        camp.JobNeeds = jobNeeds;
        tdb.Db.SaveChanges();
    }

    private static void SeedScreenedCandidate(CampaignTestDb tdb, Guid campaignId, int overallMatchScore)
    {
        tdb.Db.CvSubmissions.Add(new CvSubmission
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Email = "cand@x.com", CvParsedText = "CV",
            ParseStatus = CvParseStatus.Done, Status = CvSubmissionStatus.Analyzed,
            OverallMatchScore = overallMatchScore,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.SaveChanges();
    }

    private static List<JobNeedInput> OneInput() => new()
    {
        new() { Category = JobNeedCategories.Technical, Text = "Thạo .NET production" },
    };

    // (1) Draft → 200 (không đổi).
    [Fact]
    public async Task B6_Draft_thi_cho_sua()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);

        var res = await NewService(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, camp.Id, OneInput(), default);
        Assert.Single(res.JobNeeds);
    }

    // RNK1 · HĐ-8 — response của ReplaceJobNeedsAsync PHẢI mang questionBank ĐÚNG (nạp c.Questions).
    // Trước fix: load `FirstOrDefaultAsync` trần ⇒ c.Questions rỗng ⇒ questionBank.total = 0 + cảnh
    // báo GIẢ "questions_per_session > số câu trong bộ (0)" cho campaign có câu hỏi thật (chạy trên Active).
    [Fact]
    public async Task Rnk1B7_ReplaceJobNeeds_Response_QuestionBank_DemDungCau()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);   // 1 câu hỏi
        SetStatus(tdb, camp.Id, CampaignStatus.Active, jobNeeds: null);   // đường cứu B6 (Active + rỗng)

        var res = await NewService(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, camp.Id, OneInput(), default);

        Assert.Equal(1, res.QuestionBank.Total);
        Assert.Empty(res.QuestionBank.Warnings);   // KHÔNG cảnh báo giả "K > total(0)"
    }

    // (2) Active + job_needs RỖNG + chưa ai được sàng → 200.
    [Fact]
    public async Task Cmp1B2_Active_needs_rong_chua_ai_sang_thi_cho_sua()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);
        SetStatus(tdb, camp.Id, CampaignStatus.Active, jobNeeds: null);

        var res = await NewService(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, camp.Id, OneInput(), default);
        Assert.Single(res.JobNeeds);
        Assert.Equal(nameof(CampaignStatus.Active), res.Status);
    }

    // (3) CA MỚI — Active + job_needs ĐÃ CÓ mục (đúng hình dạng thật: AI sinh needs lúc publish) +
    // chưa ai được sàng → NAY 200 (trước CMP1-B2 là 409 vĩnh viễn — đây chính là bug đã đo trên dev:
    // HR không bao giờ khai được isMustHave vì list luôn có nội dung ngay khi Active).
    [Fact]
    public async Task Cmp1B2_Active_needs_co_muc_nhung_chua_ai_sang_thi_cho_sua()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);
        SetStatus(tdb, camp.Id, CampaignStatus.Active, jobNeeds: new List<JobNeed>
        {
            new() { NeedId = "n1", Category = JobNeedCategories.Technical, Text = "AI đề xuất", Source = JobNeedSources.AiSuggested },
        });

        var res = await NewService(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, camp.Id,
            new List<JobNeedInput>
            {
                new() { NeedId = "n1", Category = JobNeedCategories.Technical, Text = "HR đánh dấu bắt buộc", IsMustHave = true },
            }, default);

        Assert.Single(res.JobNeeds);
        Assert.True(res.JobNeeds[0].IsMustHave);   // HR khai được điều kiện loại — tính năng dùng được
        Assert.Equal(nameof(CampaignStatus.Active), res.Status);
    }

    // (4) Closed + job_needs rỗng → 409, thông điệp nêu đúng lý do "đã Closed" (không nhắc "RỖNG").
    [Fact]
    public async Task B6_Closed_needs_rong_thi_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);
        SetStatus(tdb, camp.Id, CampaignStatus.Closed, jobNeeds: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, camp.Id, OneInput(), default));

        Assert.Contains("Closed", ex.Message);
        Assert.DoesNotContain("RỖNG", ex.Message);   // luật mới không còn nói về job_needs rỗng
    }

    // (5) Active + job_needs RỖNG NHƯNG đã có 1 ứng viên OverallMatchScore = 80 → 409 — đây MỚI là
    // bất biến thật, và thông điệp phải nói đúng lý do "đã sàng", không nói "còn rỗng".
    [Fact]
    public async Task B6_Active_needs_rong_nhung_da_co_ung_vien_duoc_sang_thi_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);
        SetStatus(tdb, camp.Id, CampaignStatus.Active, jobNeeds: null);
        SeedScreenedCandidate(tdb, camp.Id, overallMatchScore: 80);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, camp.Id, OneInput(), default));

        Assert.Contains("1 ứng viên được sàng", ex.Message);
        Assert.Contains("đã chốt", ex.Message);
        Assert.DoesNotContain("RỖNG", ex.Message);
    }

    // (6) AI KHÔNG đề xuất isMustHave — publish luôn ép false, kể cả khi HR đã bật must-have cho một
    // dòng cũ (BuildJobNeedsAsync sinh ARRAY MỚI hoàn toàn khi AI thành công, không carry-over cờ).
    [Fact]
    public async Task Publish_AI_ep_isMustHave_false()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedDraft(tdb, owner);

        var svc = NewService(tdb.NewContext(), Suggester(
            new SuggestedJobNeed(JobNeedCategories.Technical, "Thạo .NET")));
        await svc.PublishCampaignAsync(owner, owner, camp.Id, default);

        using var check = tdb.NewContext();
        var saved = await check.Campaigns.FirstAsync(c => c.Id == camp.Id);
        Assert.All(saved.JobNeeds!, n => Assert.False(n.IsMustHave));
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
