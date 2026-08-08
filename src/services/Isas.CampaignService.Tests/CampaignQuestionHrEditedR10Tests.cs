using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// R10 — F9 ("sinh lại câu hỏi") KHÔNG được nuốt câu AI mà HR đã chỉnh.
///
/// Trước bản vá: F9 xoá MỌI row <c>source = AiGenerated</c>. Câu AI được HR ngồi biên tập lại vẫn giữ
/// nhãn <c>AiGenerated</c> (đúng theo F10 — provenance là sự thật do server sở hữu, KHÔNG đổi khi HR sửa,
/// xem <c>CampaignMergeQuestionsF10Tests.Sua_cau_AI_kem_id_thi_giu_nguyen_id_va_source</c>) nên nó bị
/// xoá cùng: mất trắng công sức, không cảnh báo, không khôi phục được.
///
/// Bản vá tách hai câu hỏi khác nhau thành hai cột: <c>Source</c> = "ai VIẾT RA câu này",
/// <c>HrEditedAt</c> = "HR có bỏ công chỉnh nó không". F9 chỉ thay nhóm <c>AiGenerated + HrEditedAt IS NULL</c>.
///
/// Khoá: (a) PUT sửa TEXT câu AI → đóng dấu; (b) không-đổi-text / chỉ-đổi-IsRequired / câu CustomHr →
/// KHÔNG đóng dấu; (c) sinh lại GIỮ câu đã chỉnh, THAY câu chưa chỉnh, GIỮ câu HR gõ tay;
/// (d) dấu bền qua nhiều lượt sinh; (e) audit nói ra số câu được giữ; (f) response lộ mốc cho FE.
/// </summary>
public class CampaignQuestionHrEditedR10Tests
{
    // ─────────────────────────── hạ tầng ───────────────────────────

    private sealed class FakeGenerator : IQuestionGenerator
    {
        private readonly string[] _questions;
        public int Calls { get; private set; }
        public FakeGenerator(params string[] questions) => _questions = questions;

        /// <summary>SEN1 — mức HR đặt cấp chiến dịch, ghi lại để test khẳng định được nó đi tới đây.</summary>
        public string? LastSeniority { get; private set; }

        public Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_questions.ToList());
        }

        // SEN1 — thành viên BẮT BUỘC (interface cố ý không có default): quên cài = vỡ biên dịch,
        // thay vì đánh rơi seniority trong im lặng.
        public Task<List<string>> GenerateAsync(
            string jobCategory, string? jdText, int? count, string seniority, CancellationToken ct)
        {
            LastSeniority = seniority;
            return GenerateAsync(jobCategory, jdText, count, ct);
        }
    }

    private static CampaignSvc NewService(CampaignDbContext db, IQuestionGenerator? gen = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>(),
            sessionClient: null, invitationOptions: null, questionGenerator: gen);

    private static CampaignQuestion Question(string text, QuestionSource source, DateTime? hrEditedAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            QuestionText = text,
            Source = source,
            IsRequired = true,
            CreatedAt = DateTime.UtcNow,
            HrEditedAt = hrEditedAt
        };

    private static Campaign SeedCampaign(CampaignTestDb tdb, Guid org, params CampaignQuestion[] questions)
    {
        var campaign = CampaignTestDb.NewCampaign(org, CampaignStatus.Draft);
        campaign.JDText = "Tuyển Backend .NET: EF Core, PostgreSQL, RabbitMQ.";
        campaign.Domain = "BE";
        foreach (var q in questions)
        {
            q.CampaignId = campaign.Id;
            q.OrgId = org;
            campaign.Questions.Add(q);
        }
        using var db = tdb.NewContext();
        db.Campaigns.Add(campaign);
        db.SaveChanges();
        return campaign;
    }

    // ───────── (a) PUT sửa TEXT câu AI → đóng dấu ─────────

    [Fact]
    public async Task Sua_text_cau_AI_thi_dong_dau_HrEditedAt()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai = Question("Câu AI gốc", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, ai);

        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = ai.Id, QuestionText = "Câu AI đã được HR biên tập lại cho sát JD" }
            }, default);
        }

        using var check = tdb.NewContext();
        var row = await check.CampaignQuestions.SingleAsync(q => q.Id == ai.Id);
        Assert.NotNull(row.HrEditedAt);
        // F10 giữ nguyên: đóng dấu KHÔNG được đổi nhãn nguồn (badge "AI sinh" phải sống sót).
        Assert.Equal(QuestionSource.AiGenerated, row.Source);
    }

    // ───────── (b) các ca KHÔNG được đóng dấu ─────────

    [Fact]
    public async Task Gui_lai_dung_nguyen_van_thi_KHONG_dong_dau()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai = Question("Câu AI gốc", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, ai);

        // FE round-trip (đọc response → PUT nguyên xi) là chuyện thường: mở form rồi bấm Lưu mà không
        // sửa gì. Đóng dấu ở đây sẽ làm MỌI câu AI bất tử chỉ vì HR từng mở form một lần.
        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = ai.Id, QuestionText = "Câu AI gốc" }
            }, default);
        }

        using var check = tdb.NewContext();
        Assert.Null((await check.CampaignQuestions.SingleAsync(q => q.Id == ai.Id)).HrEditedAt);
    }

    [Fact]
    public async Task Chi_doi_IsRequired_thi_KHONG_dong_dau()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai = Question("Câu AI gốc", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, ai);

        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = ai.Id, QuestionText = "Câu AI gốc", IsRequired = false }
            }, default);
        }

        using var check = tdb.NewContext();
        var row = await check.CampaignQuestions.SingleAsync(q => q.Id == ai.Id);
        // Cái HR mất khi sinh lại là CÂU CHỮ họ soạn. Gạt một checkbox không phải công sức cần bảo vệ —
        // giữ câu AI sống sót vì lý do đó sẽ làm đề tồn đọng câu cũ mà HR không hiểu vì sao.
        Assert.Null(row.HrEditedAt);
        Assert.False(row.IsRequired);   // nhưng thay đổi vẫn được ghi
    }

    [Fact]
    public async Task Sua_cau_CustomHr_thi_KHONG_dong_dau()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var hr = Question("Câu HR tự gõ", QuestionSource.CustomHr);
        var campaign = SeedCampaign(tdb, org, hr);

        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = hr.Id, QuestionText = "Câu HR sửa lại" }
            }, default);
        }

        using var check = tdb.NewContext();
        // Câu CustomHr vốn đã được F9 giữ (F9 chỉ đụng nhóm AiGenerated) → cột này vô nghĩa với nó.
        Assert.Null((await check.CampaignQuestions.SingleAsync(q => q.Id == hr.Id)).HrEditedAt);
    }

    // ───────── (c) HÀNH VI CỐT LÕI: sinh lại giữ câu AI đã chỉnh ─────────

    [Fact]
    public async Task Sinh_lai_GIU_cau_AI_da_chinh_THAY_cau_AI_chua_chinh_GIU_cau_HR()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var aiEdited = Question("Câu AI HR đã biên tập", QuestionSource.AiGenerated, hrEditedAt: DateTime.UtcNow);
        var aiUntouched = Question("Câu AI chưa ai đụng", QuestionSource.AiGenerated);
        var hr = Question("Câu HR tự gõ", QuestionSource.CustomHr);
        var campaign = SeedCampaign(tdb, org, aiEdited, aiUntouched, hr);

        await NewService(tdb.NewContext(), new FakeGenerator("Câu AI mới 1", "Câu AI mới 2"))
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();

        // 1 AI-đã-chỉnh + 1 HR + 2 AI mới = 4; câu AI chưa ai đụng bị thay.
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, r => r.Id == aiEdited.Id && r.QuestionText == "Câu AI HR đã biên tập");
        Assert.Contains(rows, r => r.Id == hr.Id);
        Assert.DoesNotContain(rows, r => r.Id == aiUntouched.Id);
        Assert.Equal(2, rows.Count(r => r.Source == QuestionSource.AiGenerated && r.HrEditedAt is null));
    }

    [Fact]
    public async Task Sua_text_roi_sinh_lai_KHONG_mat_cau_da_sua()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai = Question("Câu AI gốc", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, ai);

        // Đúng chuỗi thao tác của HR trong ô task R10: sửa text → bấm "sinh lại".
        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = ai.Id, QuestionText = "Bản HR sửa: hỏi sâu về EF Core change tracking" }
            }, default);
        }

        await NewService(tdb.NewContext(), new FakeGenerator("Câu AI mới"))
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.QuestionText == "Bản HR sửa: hỏi sâu về EF Core change tracking");
        Assert.Contains(rows, r => r.QuestionText == "Câu AI mới");
    }

    [Fact]
    public async Task Sua_ve_dung_nguyen_van_cu_van_giu_dau_da_dong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai = Question("Câu AI gốc", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, ai);

        using (var db = tdb.NewContext())
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id,
                new List<QuestionItem> { new() { Id = ai.Id, QuestionText = "Bản sửa" } }, default);
        using (var db = tdb.NewContext())
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id,
                new List<QuestionItem> { new() { Id = ai.Id, QuestionText = "Câu AI gốc" } }, default);

        using var check = tdb.NewContext();
        // Dấu KHÔNG được gỡ khi text quay về như cũ: gỡ được thì phải biết "nguyên văn AI" là gì, mà hệ
        // thống không lưu bản gốc ở đâu cả. Giữ dấu là hướng sai AN TOÀN (giữ nhầm một câu > mất một câu).
        Assert.NotNull((await check.CampaignQuestions.SingleAsync(q => q.Id == ai.Id)).HrEditedAt);
    }

    // ───────── (d) bền qua nhiều lượt sinh ─────────

    [Fact]
    public async Task Bam_sinh_nhieu_lan_van_giu_cau_da_chinh_va_khong_cong_don()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var aiEdited = Question("Câu AI HR đã chỉnh", QuestionSource.AiGenerated, hrEditedAt: DateTime.UtcNow);
        var campaign = SeedCampaign(tdb, org, aiEdited);
        var gen = new FakeGenerator("A", "B");

        await NewService(tdb.NewContext(), gen).GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default);
        await NewService(tdb.NewContext(), gen).GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();
        // 1 giữ + 2 mới ở CẢ HAI lượt → 3, không cộng dồn thành 5.
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Id == aiEdited.Id);
    }

    // ───────── (e) audit nói ra phần giữ lại ─────────

    [Fact]
    public async Task Audit_noi_ro_so_cau_AI_duoc_giu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org,
            Question("Đã chỉnh", QuestionSource.AiGenerated, hrEditedAt: DateTime.UtcNow),
            Question("Chưa chỉnh", QuestionSource.AiGenerated));

        await NewService(tdb.NewContext(), new FakeGenerator("Mới"))
            .GenerateCampaignQuestionsAsync(org, org, campaign.Id, null, default);

        using var check = tdb.NewContext();
        var audit = await check.AuditLogs
            .Where(a => a.EntityId == campaign.Id && a.Action == AuditAction.EditQuestions)
            .SingleAsync();
        // "Thay N câu AI cũ" mà im lặng về phần giữ thì HR đọc lại không phân biệt được "AI sinh ít câu"
        // với "một số câu bị giữ vì đã chỉnh".
        Assert.Contains("thay 1 câu AI cũ", audit.Summary);
        Assert.Contains("giữ 1 câu AI HR đã chỉnh", audit.Summary);
    }

    // ───────── (f) response lộ mốc cho FE ─────────

    [Fact]
    public async Task Response_tra_hrEditedAt_de_FE_dem_dung_hop_thoai_xac_nhan()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai = Question("Câu AI gốc", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, ai);

        using var db = tdb.NewContext();
        var res = await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id,
            new List<QuestionItem> { new() { Id = ai.Id, QuestionText = "HR sửa" } }, default);

        // FE đang đếm "N câu AI sẽ bị THAY" theo `source` ⇒ vẫn xếp câu này vào nhóm sẽ-bị-thay (dương
        // tính giả). Field này là thứ FE cần để sửa; additive nên FE cũ không vỡ.
        Assert.NotNull(res.Questions.Single(q => q.Id == ai.Id).HrEditedAt);
    }
}
