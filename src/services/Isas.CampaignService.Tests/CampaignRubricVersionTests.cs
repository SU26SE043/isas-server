using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-18 — <c>campaigns.rubric_version</c> là ĐỊNH DANH bộ thước đo, và luật bump.
///
/// <para>Vì sao cần: cho HR sửa mốc trên campaign đang chạy mà không có nhãn version thì điểm của
/// ứng viên thi trước và thi sau bị đem so thẳng ở bảng xếp hạng (CAMP-10) — hai thước đo, một cột
/// điểm, không ai thấy. Cùng lớp vấn đề mà <c>scoring_scope_version</c> đã sinh ra để chặn.</para>
///
/// <para>Bump SAI cũng hỏng ngang bump THIẾU: mỗi lần HR bấm Lưu để sửa tiêu đề mà version nhảy thì
/// nhãn mất hết ý nghĩa và FE nổi băng "khác thước đo" giả.</para>
/// </summary>
public class CampaignRubricVersionTests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static CampaignCriterion SeedCriterion(
        Guid campaignId, int order, string name, decimal weight, int maxScore = 5)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = order, Name = name,
            Weight = weight, MaxScore = maxScore, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    private static CampaignCriterionLevel SeedLevel(Guid criterionId, int score, string descriptor)
        => new()
        {
            Id = Guid.NewGuid(), CriterionId = criterionId, Score = score, Descriptor = descriptor,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    // ── Vân tay ──────────────────────────────────────────────────────────

    // ⚠ Bẫy decimal: decimal GIỮ SCALE. Bộ vừa dựng trong bộ nhớ có 0.5m, bộ đọc từ numeric(5,4) có
    // 0.5000m — cùng giá trị, khác chuỗi. Nếu vân tay serialize decimal thô thì MỌI lần Lưu trên
    // campaign Active đều bị coi là "đã đổi thước đo" ⇒ version nhảy vô tội vạ.
    [Fact]
    public void Vantay_khong_doi_khi_weight_khac_scale_nhung_bang_gia_tri()
    {
        var cid = Guid.NewGuid();
        var trongBoNho = new[] { SeedCriterion(cid, 0, "A", 0.5m), SeedCriterion(cid, 1, "B", 0.5m) };
        var docTuDb = new[] { SeedCriterion(cid, 0, "A", 0.5000m), SeedCriterion(cid, 1, "B", 0.5000m) };

        Assert.Equal(RubricFingerprint.Compute(trongBoNho), RubricFingerprint.Compute(docTuDb));
    }

    [Fact]
    public void Vantay_doi_khi_moc_diem_doi()
    {
        var cid = Guid.NewGuid();
        var truoc = SeedCriterion(cid, 0, "A", 1.0m);
        truoc.Levels = new List<CampaignCriterionLevel> { SeedLevel(truoc.Id, 0, "Không nói được gì") };

        var sau = SeedCriterion(cid, 0, "A", 1.0m);
        sau.Id = truoc.Id;
        sau.Levels = new List<CampaignCriterionLevel> { SeedLevel(truoc.Id, 0, "CÓ: nêu được khái niệm") };

        Assert.NotEqual(RubricFingerprint.Compute(new[] { truoc }), RubricFingerprint.Compute(new[] { sau }));
    }

    // Vế LÕI (includeLevels=false) là thứ phân biệt "HR chỉ sửa mốc" (được phép khi Active) với
    // "HR sửa chính bộ tiêu chí" (CAMP-2: chỉ Draft). Đổi mốc KHÔNG được làm vân tay lõi nhúc nhích.
    [Fact]
    public void Vantay_LOI_bo_qua_moc_diem()
    {
        var cid = Guid.NewGuid();
        var truoc = SeedCriterion(cid, 0, "A", 1.0m);
        truoc.Levels = new List<CampaignCriterionLevel> { SeedLevel(truoc.Id, 0, "cũ") };
        var sau = SeedCriterion(cid, 0, "A", 1.0m);
        sau.Levels = new List<CampaignCriterionLevel> { SeedLevel(sau.Id, 0, "mới hoàn toàn khác") };

        Assert.Equal(
            RubricFingerprint.Compute(new[] { truoc }, includeLevels: false),
            RubricFingerprint.Compute(new[] { sau }, includeLevels: false));
    }

    [Fact]
    public void Vantay_doi_khi_ten_hoac_thang_diem_doi()
    {
        var cid = Guid.NewGuid();
        var goc = new[] { SeedCriterion(cid, 0, "A", 1.0m, maxScore: 5) };

        Assert.NotEqual(RubricFingerprint.Compute(goc),
            RubricFingerprint.Compute(new[] { SeedCriterion(cid, 0, "B", 1.0m, maxScore: 5) }));
        Assert.NotEqual(RubricFingerprint.Compute(goc),
            RubricFingerprint.Compute(new[] { SeedCriterion(cid, 0, "A", 1.0m, maxScore: 10) }));
    }

    // Vân tay phải ổn định trước thứ tự phần tử trong bộ nhớ (EF trả theo thứ tự nào là chuyện của DB).
    [Fact]
    public void Vantay_on_dinh_truoc_thu_tu_phan_tu()
    {
        var cid = Guid.NewGuid();
        var a = SeedCriterion(cid, 0, "A", 0.5m);
        var b = SeedCriterion(cid, 1, "B", 0.5m);
        Assert.Equal(RubricFingerprint.Compute(new[] { a, b }), RubricFingerprint.Compute(new[] { b, a }));
    }

    // ── Luật bump ────────────────────────────────────────────────────────

    [Fact]
    public async Task Campaign_moi_tao_co_rubric_version_1()
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
            Questions = new List<QuestionItem>()
        }, default);

        Assert.Equal(1, res.RubricVersion);
        Assert.Null(res.RubricVersionUpdatedAt);
        Assert.Null(res.RubricVersionUpdatedBy);
    }

    // Draft = chưa ai bị chấm, Interview chưa materialize gì ⇒ sửa thoải mái, KHÔNG bump.
    [Fact]
    public async Task Draft_sua_tieu_chi_KHONG_bump_version()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignCriteria.Add(SeedCriterion(camp.Id, 0, "Cũ", 1.0m));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { new() { Name = "Mới hoàn toàn", Weight = 1.0m, MaxScore = 5 } }
        }, default);

        using var check = tdb.NewContext();
        var after = await check.Campaigns.FirstAsync(c => c.Id == camp.Id);
        Assert.Equal(1, after.RubricVersion);
        Assert.Null(after.RubricVersionUpdatedAt);
    }

    // CAMP-1/CAMP-2: campaign đã đóng thì thước đo là dữ liệu lịch sử — sửa được nghĩa là sửa lại
    // được cách chấm của một cuộc tuyển đã kết thúc.
    [Theory]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task Campaign_dong_sua_tieu_chi_nem_409(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, status);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignCriteria.Add(SeedCriterion(camp.Id, 0, "Giữ", 1.0m));
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateCampaignAsync(owner, owner, camp.Id, new UpdateCampaignRequest
            {
                Criteria = new List<CriterionItem> { new() { Name = "Mới", Weight = 1.0m, MaxScore = 5 } }
            }, default));

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal("Giữ", Assert.Single(rows).Name);
        Assert.Equal(1, (await check.Campaigns.FirstAsync(c => c.Id == camp.Id)).RubricVersion);
    }
}
