using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// F10 (FR11) — trộn câu hỏi AI (F9) + câu hỏi HR gõ tay qua `PUT /campaign/{id}/questions`.
///
/// Vấn đề F10 sửa: `UpdateCampaignQuestionsAsync` cũ gọi `Questions.Clear()` rồi dựng lại toàn bộ với
/// Guid mới, `source` lấy thẳng từ client — mà FE hardcode `source:'CustomHr'` ⇒ HR sửa MỘT câu là
/// (a) mọi câu F9 sinh mất nhãn `AiGenerated`, (b) mọi id câu hỏi đổi.
///
/// Khoá các hành vi:
/// (a) gửi kèm id → câu giữ NGUYÊN id, `source`, `created_at`; chỉ text/isRequired đổi;
/// (b) trộn: sửa câu HR + giữ câu AI + thêm câu mới trong CÙNG một lần PUT;
/// (c) `source` client gửi bị BỎ QUA — không thể tự phong `AiGenerated` (create lẫn update);
/// (d) câu vắng mặt trong payload → xoá (PUT = replace);
/// (e) id lạ / id trùng trong payload → 400 (ArgumentException), KHÔNG ghi gì;
/// (f) CAMP-2: campaign ≠ Draft → 409 (InvalidOperationException);
/// (g) campaign ngoài org → 404 (KeyNotFoundException);
/// (h) round-trip: đọc response → PUT nguyên xi → đề không đổi (hợp đồng FE echo id).
/// </summary>
public class CampaignMergeQuestionsF10Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>(),
            sessionClient: null, invitationOptions: null, questionGenerator: null);

    private static CampaignQuestion Question(string text, QuestionSource source, DateTime? createdAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            QuestionText = text,
            Source = source,
            IsRequired = true,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };

    private static Campaign SeedCampaign(
        CampaignTestDb tdb, Guid org,
        CampaignStatus status = CampaignStatus.Draft,
        params CampaignQuestion[] questions)
    {
        var campaign = CampaignTestDb.NewCampaign(org, status);
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

    // ───────────────── (a) gửi id → giữ id + source + created_at ─────────────────

    [Fact]
    public async Task Sua_cau_AI_kem_id_thi_giu_nguyen_id_va_source()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai = Question("Câu AI gốc", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft, ai);

        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                // FE gửi source CustomHr (hardcode) — server phải PHỚT LỜ.
                new() { Id = ai.Id, QuestionText = "Câu AI đã được HR biên tập", Source = QuestionSource.CustomHr }
            }, default);
        }

        using var check = tdb.NewContext();
        var row = Assert.Single(await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync());

        Assert.Equal(ai.Id, row.Id);                                  // id KHÔNG đổi
        Assert.Equal(QuestionSource.AiGenerated, row.Source);          // provenance KHÔNG đổi ← hành vi cốt lõi F10
        Assert.Equal("Câu AI đã được HR biên tập", row.QuestionText);   // nội dung ĐÃ đổi
    }

    [Fact]
    public async Task Sua_cau_kem_id_khong_lam_doi_created_at()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        // CreatedAt quyết định thứ tự bài thi (ParticipationService: OrderBy CreatedAt, Id).
        var old = DateTime.UtcNow.AddDays(-3);
        var q = Question("Câu cũ", QuestionSource.AiGenerated, old);
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft, q);

        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = q.Id, QuestionText = "Câu cũ (sửa)" }
            }, default);
        }

        using var check = tdb.NewContext();
        var row = await check.CampaignQuestions.FirstAsync(x => x.Id == q.Id);
        Assert.Equal(old, row.CreatedAt, TimeSpan.FromSeconds(1));
    }

    // ───────────────── (b) trộn AI + HR + thêm mới trong 1 lần PUT ─────────────────

    [Fact]
    public async Task Tron_giu_cau_AI_sua_cau_HR_them_cau_moi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai1 = Question("AI 1", QuestionSource.AiGenerated);
        var ai2 = Question("AI 2", QuestionSource.AiGenerated);
        var hr1 = Question("HR 1", QuestionSource.CustomHr);
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft, ai1, ai2, hr1);

        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = ai1.Id, QuestionText = "AI 1" },                    // giữ nguyên
                new() { Id = ai2.Id, QuestionText = "AI 2 (HR sửa lại)" },       // sửa text
                new() { Id = hr1.Id, QuestionText = "HR 1" },                    // giữ nguyên
                new() { QuestionText = "HR gõ thêm câu mới" }                    // thêm (không id)
            }, default);
        }

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();

        Assert.Equal(4, rows.Count);
        // Đây là đích của F10: hai loại nguồn CÙNG TỒN TẠI sau khi HR sửa qua UI.
        Assert.Equal(2, rows.Count(r => r.Source == QuestionSource.AiGenerated));
        Assert.Equal(2, rows.Count(r => r.Source == QuestionSource.CustomHr));
        Assert.Equal("AI 2 (HR sửa lại)", rows.Single(r => r.Id == ai2.Id).QuestionText);
        Assert.Equal(QuestionSource.CustomHr, rows.Single(r => r.QuestionText == "HR gõ thêm câu mới").Source);
    }

    [Fact]
    public async Task Cau_AI_song_sot_qua_nhieu_lan_PUT_lien_tiep()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai = Question("Câu AI", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft, ai);

        // HR bấm Lưu 3 lần liên tiếp (kịch bản thật: sửa → lưu → sửa tiếp → lưu…).
        for (int i = 1; i <= 3; i++)
        {
            using var db = tdb.NewContext();
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = ai.Id, QuestionText = "Câu AI" },
                new() { QuestionText = $"HR câu {i}" }
            }, default);
        }

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();
        // Mỗi lượt chỉ gửi 1 câu HR không-id → câu HR lượt trước bị xoá (PUT=replace), câu AI sống.
        Assert.Equal(2, rows.Count);
        Assert.Equal(ai.Id, rows.Single(r => r.Source == QuestionSource.AiGenerated).Id);
        Assert.Equal("HR câu 3", rows.Single(r => r.Source == QuestionSource.CustomHr).QuestionText);
    }

    // ───────────────── (c) source client gửi bị bỏ qua ─────────────────

    [Fact]
    public async Task Client_khong_the_tu_phong_AiGenerated_khi_them_moi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft, Question("Có sẵn", QuestionSource.CustomHr));

        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { QuestionText = "Tôi tự nhận là AI sinh", Source = QuestionSource.AiGenerated }
            }, default);
        }

        using var check = tdb.NewContext();
        var row = Assert.Single(await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync());
        // Chỉ đường F9 mới được đặt AiGenerated. Nếu lọt, nhãn nguồn thành lời khai tự do của client —
        // và lượt F9 kế sẽ XOÁ nhầm câu này (F9 remove mọi row AiGenerated).
        Assert.Equal(QuestionSource.CustomHr, row.Source);
    }

    [Fact]
    public async Task Client_khong_the_tu_phong_AiGenerated_khi_tao_campaign()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        using (var db = tdb.NewContext())
        {
            var res = await NewService(db).CreateCampaignAsync(org, org, new CreateCampaignRequest
            {
                Title = "C", Domain = "BE", TimeLimitMinutes = 30,
                StartsAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7),
                Questions = new List<QuestionItem>
                {
                    new() { QuestionText = "Câu tự nhận AI", Source = QuestionSource.AiGenerated }
                }
            }, default);
            Assert.Equal("CustomHr", Assert.Single(res.Questions).Source);
        }

        using var check = tdb.NewContext();
        Assert.All(await check.CampaignQuestions.ToListAsync(),
            r => Assert.Equal(QuestionSource.CustomHr, r.Source));
    }

    // ───────────────── (d) vắng mặt → xoá ─────────────────

    [Fact]
    public async Task Cau_vang_mat_trong_payload_bi_xoa()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var keep = Question("Giữ", QuestionSource.AiGenerated);
        var drop = Question("HR xoá câu này trên UI", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft, keep, drop);

        using (var db = tdb.NewContext())
        {
            await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = keep.Id, QuestionText = "Giữ" }
            }, default);
        }

        using var check = tdb.NewContext();
        var rows = await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync();
        Assert.Equal(keep.Id, Assert.Single(rows).Id);
    }

    // ───────────────── (e) id lạ / id trùng → 400, không ghi gì ─────────────────

    [Fact]
    public async Task Id_khong_thuoc_campaign_thi_400_va_khong_ghi_gi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var mine = Question("Của tôi", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft, mine);
        // Câu của campaign KHÁC (cùng org) — không được sửa xuyên campaign.
        var other = Question("Của campaign khác", QuestionSource.CustomHr);
        SeedCampaign(tdb, org, CampaignStatus.Draft, other);

        using (var db = tdb.NewContext())
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
                {
                    new() { Id = mine.Id, QuestionText = "Của tôi" },
                    new() { Id = other.Id, QuestionText = "Cướp câu của campaign khác" }
                }, default));
        }

        using var check = tdb.NewContext();
        // Không ghi một phần: câu của campaign kia còn nguyên chỗ cũ.
        Assert.Equal("Của campaign khác",
            (await check.CampaignQuestions.FirstAsync(q => q.Id == other.Id)).QuestionText);
        Assert.NotEqual(campaign.Id, (await check.CampaignQuestions.FirstAsync(q => q.Id == other.Id)).CampaignId);
    }

    [Fact]
    public async Task Id_trung_lap_trong_payload_thi_400()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var q = Question("Câu", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft, q);

        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
            {
                new() { Id = q.Id, QuestionText = "Bản A" },
                new() { Id = q.Id, QuestionText = "Bản B" }
            }, default));
    }

    // ───────────────── (f) CAMP-2 · (g) org isolation ─────────────────

    [Theory]
    [InlineData(CampaignStatus.Active)]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Campaign_khong_phai_Draft_thi_409_va_khong_doi_cau_hoi(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var ai = Question("Đề đang chạy", QuestionSource.AiGenerated);
        var campaign = SeedCampaign(tdb, org, status, ai);

        using (var db = tdb.NewContext())
        {
            // CAMP-2: đổi đề của chiến dịch đã publish = đổi bài thi dưới chân ứng viên đang làm.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id, new List<QuestionItem>
                {
                    new() { Id = ai.Id, QuestionText = "Đề bị đổi giữa chừng" }
                }, default));
        }

        using var check = tdb.NewContext();
        var row = Assert.Single(await check.CampaignQuestions.Where(q => q.CampaignId == campaign.Id).ToListAsync());
        Assert.Equal("Đề đang chạy", row.QuestionText);
    }

    [Fact]
    public async Task Campaign_ngoai_org_thi_404()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft, Question("Q", QuestionSource.CustomHr));

        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(db).UpdateCampaignQuestionsAsync(Guid.NewGuid(), org, campaign.Id, new List<QuestionItem>
            {
                new() { QuestionText = "Q" }
            }, default));
    }

    // ───────────────── (h) round-trip hợp đồng FE ─────────────────

    [Fact]
    public async Task Doc_response_roi_PUT_nguyen_xi_thi_de_khong_doi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var campaign = SeedCampaign(tdb, org, CampaignStatus.Draft,
            Question("AI 1", QuestionSource.AiGenerated, DateTime.UtcNow.AddMinutes(-5)),
            Question("HR 1", QuestionSource.CustomHr, DateTime.UtcNow.AddMinutes(-4)));

        CampaignResponse before;
        using (var db = tdb.NewContext())
            before = await NewService(db).GetCampaignAsync(org, campaign.Id, default);

        CampaignResponse after;
        using (var db = tdb.NewContext())
            after = await NewService(db).UpdateCampaignQuestionsAsync(org, org, campaign.Id,
                // Đúng thứ FE cầm trong tay: id + text + source từ response trước đó.
                before.Questions.Select(q => new QuestionItem
                {
                    Id = q.Id,
                    QuestionText = q.QuestionText,
                    IsRequired = q.IsRequired
                }).ToList(), default);

        Assert.Equal(
            before.Questions.Select(q => (q.Id, q.QuestionText, q.Source)),
            after.Questions.Select(q => (q.Id, q.QuestionText, q.Source)));
    }
}
