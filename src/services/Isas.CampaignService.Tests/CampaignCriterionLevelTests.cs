using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-16/17 — đọc-ghi MỐC ĐIỂM qua <c>PUT /campaign/{id}</c>.
///
/// <para>Nhóm test quan trọng nhất là carry-over: PUT criteria là replace-all MINT ID MỚI, nên nếu
/// <c>levels = null</c> bị hiểu là "xoá" thì MỘT lần HR bấm Lưu trên bản FE cũ là mất trắng mốc của
/// cả chiến dịch — và mất mốc KHÔNG có triệu chứng, Interview lặng lẽ rơi về dải mặc định rồi vẫn
/// chấm ra điểm.</para>
/// </summary>
public class CampaignCriterionLevelTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private const string D0 = "CÓ: không nêu được ý nào | CÒN THIẾU: toàn bộ nội dung câu hỏi";
    private const string D5 = "CÓ: nêu đúng khái niệm và ví dụ | CÒN THIẾU: không có đánh đổi";
    private const string D5Moi = "CÓ: nêu khái niệm, ví dụ, đánh đổi | CÒN THIẾU: chưa nói giới hạn";

    /// <summary>Seed 1 campaign + 1 tiêu chí "Chuyên môn" (maxScore 5) kèm 2 mốc 0/5.</summary>
    private static async Task<(Campaign Camp, Guid CriterionId)> SeedAsync(
        CampaignTestDb tdb, Guid owner, CampaignStatus status)
    {
        var camp = CampaignTestDb.NewCampaign(owner, status);
        tdb.Db.Campaigns.Add(camp);
        var cr = new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Chuyên môn",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CampaignCriteria.Add(cr);
        tdb.Db.CampaignCriterionLevels.AddRange(
            NewLevel(cr.Id, 0, D0), NewLevel(cr.Id, 5, D5));
        await tdb.Db.SaveChangesAsync();
        return (camp, cr.Id);
    }

    private static CampaignCriterionLevel NewLevel(Guid criterionId, int score, string descriptor)
        => new()
        {
            Id = Guid.NewGuid(), CriterionId = criterionId, Score = score, Descriptor = descriptor,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    private static async Task<List<CampaignCriterionLevel>> ReadLevelsAsync(CampaignTestDb tdb, Guid campaignId)
    {
        using var check = tdb.NewContext();
        return await check.CampaignCriterionLevels
            .Where(l => l.Criterion.CampaignId == campaignId)
            .OrderBy(l => l.Score)
            .ToListAsync();
    }

    private static CriterionItem Item(string name, List<CriterionLevelItem>? levels, int maxScore = 5)
        => new() { Name = name, Weight = 1.0m, MaxScore = maxScore, Levels = levels };

    private static List<CriterionLevelItem> Levels(params (int Score, string Descriptor)[] items)
        => items.Select(i => new CriterionLevelItem { Score = i.Score, Descriptor = i.Descriptor }).ToList();

    // ── (a) TEST QUAN TRỌNG NHẤT: không gửi levels ⇒ mốc CÒN NGUYÊN ────────

    [Fact]
    public async Task PUT_khong_gui_levels_thi_moc_CON_NGUYEN()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var svc = NewService(tdb.NewContext());
        // Đúng hình dạng payload của một bản FE CŨ: có criteria, không hề biết field levels.
        await svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Item("Chuyên môn", levels: null) }
        }, default);

        var levels = await ReadLevelsAsync(tdb, camp.Id);
        Assert.Equal(2, levels.Count);
        Assert.Equal(new[] { 0, 5 }, levels.Select(l => l.Score));
        Assert.Equal(D0, levels[0].Descriptor);
        Assert.Equal(D5, levels[1].Descriptor);
    }

    // ── (b) [] = XOÁ ──────────────────────────────────────────────────────

    [Fact]
    public async Task PUT_gui_mang_rong_thi_XOA_het_moc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var svc = NewService(tdb.NewContext());
        await svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Item("Chuyên môn", new List<CriterionLevelItem>()) }
        }, default);

        Assert.Empty(await ReadLevelsAsync(tdb, camp.Id));
    }

    [Fact]
    public async Task PUT_gui_bo_moc_moi_thi_THAY_THE()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var svc = NewService(tdb.NewContext());
        await svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem>
            {
                Item("Chuyên môn", Levels((0, D0), (3, D5), (5, D5Moi)))
            }
        }, default);

        var levels = await ReadLevelsAsync(tdb, camp.Id);
        Assert.Equal(new[] { 0, 3, 5 }, levels.Select(l => l.Score));
        Assert.Equal(D5Moi, levels[2].Descriptor);
    }

    // ── (c) đổi TÊN mà không gửi levels ⇒ mốc MẤT (khoá hành vi đã biết) ──

    // Carry-over ghép theo TÊN vì id bị mint mới ở replace-all. Test này KHÔNG khẳng định đây là
    // hành vi mong muốn — nó ghim lại hệ quả để FE biết phải luôn gửi levels khi người dùng sửa tên,
    // và để ai đổi cách ghép sau này thấy ngay mình đang đổi cái gì.
    [Fact]
    public async Task Doi_TEN_tieu_chi_ma_khong_gui_levels_thi_moc_MAT()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var svc = NewService(tdb.NewContext());
        await svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Item("Chuyên môn sâu", levels: null) }
        }, default);

        Assert.Empty(await ReadLevelsAsync(tdb, camp.Id));
    }

    // Ghép tên KHÔNG phân biệt hoa/thường — HR sửa "chuyên môn" → "Chuyên Môn" không đáng mất mốc.
    [Fact]
    public async Task Carry_over_ghep_ten_khong_phan_biet_hoa_thuong()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var svc = NewService(tdb.NewContext());
        await svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Item("CHUYÊN MÔN", levels: null) }
        }, default);

        Assert.Equal(2, (await ReadLevelsAsync(tdb, camp.Id)).Count);
    }

    // ── (d)(e)(f) validate CAMP-17 ────────────────────────────────────────

    // ⚠ Thiếu mốc 0 KHÔNG làm lỗi nào nổ ở đường chấm: bài trống snap về mốc thấp nhất đang có
    // ⇒ ứng viên không nói gì vẫn được 3/5, và bảng xếp hạng sai mà không ai thấy.
    [Fact]
    public async Task Thieu_moc_0_thi_400()
    {
        var ex = await AssertCriteriaRejectedAsync(Levels((3, D0), (5, D5)));
        Assert.Contains("mốc 0", ex.Message);
    }

    [Fact]
    public async Task Thieu_moc_maxScore_thi_400()
    {
        var ex = await AssertCriteriaRejectedAsync(Levels((0, D0), (3, D5)));
        Assert.Contains("mốc 5", ex.Message);
    }

    [Fact]
    public async Task Score_vuot_maxScore_thi_400()
    {
        var ex = await AssertCriteriaRejectedAsync(Levels((0, D0), (5, D5), (9, D5Moi)));
        Assert.Contains("[0, 5]", ex.Message);
    }

    [Fact]
    public async Task Score_am_thi_400()
    {
        var ex = await AssertCriteriaRejectedAsync(Levels((-1, D0), (0, D5), (5, D5Moi)));
        Assert.Contains("[0, 5]", ex.Message);
    }

    // Trùng score là LÝ DO bảng con tồn tại thay vì jsonb — việc snap điểm về mức gần nhất trở nên
    // không xác định ⇒ chấm sai trong im lặng.
    [Fact]
    public async Task Trung_score_thi_400()
    {
        var ex = await AssertCriteriaRejectedAsync(Levels((0, D0), (5, D5), (5, D5Moi)));
        Assert.Contains("không xác định", ex.Message);
    }

    [Fact]
    public async Task Duoi_2_moc_thi_400()
    {
        var ex = await AssertCriteriaRejectedAsync(Levels((0, D0)));
        Assert.Contains("2–10 mốc", ex.Message);
    }

    [Fact]
    public async Task Tren_10_moc_thi_400()
    {
        var many = Enumerable.Range(0, 11)
            .Select(i => new CriterionLevelItem { Score = i, Descriptor = D0 }).ToList();
        var ex = await AssertCriteriaRejectedAsync(many, maxScore: 10);
        Assert.Contains("2–10 mốc", ex.Message);
    }

    [Fact]
    public async Task Descriptor_qua_ngan_thi_400()
    {
        var ex = await AssertCriteriaRejectedAsync(Levels((0, "ngắn"), (5, D5)));
        Assert.Contains("ký tự", ex.Message);
    }

    [Fact]
    public async Task Descriptor_qua_dai_thi_400()
    {
        var ex = await AssertCriteriaRejectedAsync(Levels((0, new string('x', 501)), (5, D5)));
        Assert.Contains("ký tự", ex.Message);
    }

    // Payload sai KHÔNG được để lại trạng thái nửa vời — mốc cũ phải còn nguyên.
    private static async Task<ArgumentException> AssertCriteriaRejectedAsync(
        List<CriterionLevelItem> levels, int maxScore = 5)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Draft);

        var svc = NewService(tdb.NewContext());
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
            {
                Criteria = new List<CriterionItem> { Item("Chuyên môn", levels, maxScore) }
            }, default));

        var still = await ReadLevelsAsync(tdb, camp.Id);
        Assert.Equal(2, still.Count);
        return ex;
    }

    // ── Active: chỉ MỐC được sửa, và sửa thì BUMP version ─────────────────

    [Fact]
    public async Task Active_chi_doi_moc_thi_duoc_phep_va_BUMP_version()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Active);

        var svc = NewService(tdb.NewContext());
        var actor = Guid.NewGuid();
        await svc.UpdateCampaignAsync(owner, actor, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Item("Chuyên môn", Levels((0, D0), (5, D5Moi))) }
        }, default);

        using var check = tdb.NewContext();
        var after = await check.Campaigns.FirstAsync(c => c.Id == camp.Id);
        Assert.Equal(2, after.RubricVersion);
        Assert.NotNull(after.RubricVersionUpdatedAt);
        Assert.Equal(actor, after.RubricVersionUpdatedBy);

        var levels = await ReadLevelsAsync(tdb, camp.Id);
        Assert.Equal(D5Moi, levels[1].Descriptor);

        // Audit phải kể đúng câu chuyện (ai đổi thước đo, từ v mấy sang v mấy).
        var audit = await check.AuditLogs
            .Where(a => a.EntityId == camp.Id && a.Action == AuditAction.EditCriteria)
            .OrderByDescending(a => a.At).FirstAsync();
        Assert.Equal(actor, audit.ActorUserId);
        Assert.Contains("v1 → v2", audit.Summary);
    }

    // Bump oan cũng hỏng ngang bump thiếu: HR bấm Lưu để sửa tiêu đề mà version nhảy thì nhãn thước
    // đo mất hết ý nghĩa và FE nổi băng "khác thước đo" giả.
    [Fact]
    public async Task Active_luu_ma_KHONG_doi_gi_thi_khong_bump()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Active);

        var svc = NewService(tdb.NewContext());
        await svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
        {
            Title = "Đổi mỗi tiêu đề",
            Criteria = new List<CriterionItem> { Item("Chuyên môn", Levels((0, D0), (5, D5))) }
        }, default);

        using var check = tdb.NewContext();
        Assert.Equal(1, (await check.Campaigns.FirstAsync(c => c.Id == camp.Id)).RubricVersion);
    }

    // Không gửi levels trên campaign Active = carry-over ⇒ vân tay không đổi ⇒ không bump.
    [Fact]
    public async Task Active_khong_gui_levels_thi_khong_bump_va_giu_moc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Active);

        var svc = NewService(tdb.NewContext());
        await svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Item("Chuyên môn", levels: null) }
        }, default);

        using var check = tdb.NewContext();
        Assert.Equal(1, (await check.Campaigns.FirstAsync(c => c.Id == camp.Id)).RubricVersion);
        Assert.Equal(2, (await ReadLevelsAsync(tdb, camp.Id)).Count);
    }

    // CAMP-2 giữ nguyên cho phần LÕI: đổi trọng số/thang điểm giữa chừng là đổi chính tập tiêu chí mà
    // ứng viên đã chấm xong đang được xếp hạng theo.
    [Fact]
    public async Task Active_doi_thang_diem_thi_409_va_khong_bump()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, _) = await SeedAsync(tdb, owner, CampaignStatus.Active);

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
            {
                Criteria = new List<CriterionItem>
                {
                    Item("Chuyên môn", Levels((0, D0), (10, D5)), maxScore: 10)
                }
            }, default));

        using var check = tdb.NewContext();
        Assert.Equal(1, (await check.Campaigns.FirstAsync(c => c.Id == camp.Id)).RubricVersion);
        Assert.Equal(new[] { 0, 5 }, (await ReadLevelsAsync(tdb, camp.Id)).Select(l => l.Score));
    }

    // ── Đọc ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GET_campaign_tra_moc_diem_sap_tang_dan()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (camp, cid) = await SeedAsync(tdb, owner, CampaignStatus.Draft);
        tdb.Db.CampaignCriterionLevels.Add(NewLevel(cid, 3, D5Moi));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var res = await svc.GetCampaignAsync(owner, camp.Id, default);

        var levels = Assert.Single(res.Criteria).Levels;
        Assert.Equal(new[] { 0, 3, 5 }, levels.Select(l => l.Score));
    }

    // Chưa khai mốc là trạng thái HỢP LỆ (Interview rơi về dải mặc định như trước tính năng này),
    // không phải lỗi.
    [Fact]
    public async Task Tieu_chi_chua_khai_moc_tra_mang_rong()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignCriteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Trống",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        var res = await svc.GetCampaignAsync(owner, camp.Id, default);
        Assert.Empty(Assert.Single(res.Criteria).Levels);
    }

    // Tạo campaign kèm mốc ngay từ đầu (không phải sửa hai lần).
    [Fact]
    public async Task Create_campaign_kem_moc_diem()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var svc = NewService(tdb.NewContext());

        var res = await svc.CreateCampaignAsync(owner, owner, new CreateCampaignRequest
        {
            Title = "Tuyển BE",
            Domain = "BE",
            TimeLimitMinutes = 30,
            StartsAt = DateTime.UtcNow.AddDays(1),
            ExpiresAt = DateTime.UtcNow.AddDays(10),
            Questions = new List<QuestionItem>(),
            Criteria = new List<CriterionItem> { Item("Chuyên môn", Levels((0, D0), (5, D5))) }
        }, default);

        Assert.Equal(new[] { 0, 5 }, Assert.Single(res.Criteria).Levels.Select(l => l.Score));
        Assert.Equal(2, (await ReadLevelsAsync(tdb, res.Id)).Count);
    }
}
