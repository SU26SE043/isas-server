using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP4-B1 — <c>ReplaceJobNeedsAsync</c> (PUT /campaign/{id}/job-needs) dùng CÙNG MỘT THƯỚC với khối
/// luật-lọc-cứng trong <c>UpdateCampaignAsync</c>: "campaign đã có bất kỳ <c>cv_submission</c> nào chưa"
/// (<c>AnyAsync</c>). Trước CMP4-B1 cửa này đo <c>OverallMatchScore != null</c> — chỉ ứng viên ĐÃ CÓ
/// ĐIỂM — nên ứng viên vừa upload (Filtered/Analyzing) lọt qua ⇒ HR đổi/xoá thước đo sau lưng batch
/// đang chấm ⇒ callback về sau dựng danh sách hợp lệ từ <c>job_needs</c> MỚI ⇒ mọi needId cũ bị bỏ ⇒
/// assessments rỗng ⇒ điểm null ⇒ status Analyzed: "đã phân tích xong" mà trống trơn, không exception.
///
/// <para>4 tổ hợp / 4 mã: 1 row Filtered → 409 · 1 row Analyzing → 409 · chưa có row nào → 200 ·
/// gửi <c>[]</c> (wipe) khi đã có row → 409. Wipe khi chưa có ai → vẫn 200.</para>
/// </summary>
public class CampaignJobNeedsMeasureCmp4B1Tests
{
    private static CampaignSvc NewSvc(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static Guid SeedDraftWithNeeds(CampaignTestDb tdb, Guid owner)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        camp.Domain = "BE";
        camp.JobNeeds = new List<JobNeed>
        {
            new() { NeedId = "n1", Category = JobNeedCategories.Technical, Text = "Thạo .NET", Source = JobNeedSources.HrEdited },
        };
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp.Id;
    }

    private static async Task AddCvAsync(CampaignTestDb tdb, Guid campId, CvSubmissionStatus status, int? overallMatchScore)
    {
        using var c = tdb.NewContext();
        c.CvSubmissions.Add(new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campId,
            Email = $"c{Guid.NewGuid():N}@x.com",
            CvParsedText = "CV text",
            ParseStatus = CvParseStatus.Done,
            Status = status,
            OverallMatchScore = overallMatchScore,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await c.SaveChangesAsync();
    }

    private static List<JobNeedInput> OneInput() => new()
    {
        new() { Category = JobNeedCategories.Technical, Text = "Thạo Kafka" },
    };

    // ── (1) 1 row Filtered (chưa có điểm) ⇒ PUT /job-needs 409 ─────────────────────────────────────
    // Đây là ca CMP4-B1 sinh ra để bịt: trước bản này Filtered (OverallMatchScore null) lọt qua cửa.
    [Fact]
    public async Task Filtered_row_thi_ReplaceJobNeeds_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraftWithNeeds(tdb, owner);
        await AddCvAsync(tdb, campId, CvSubmissionStatus.Filtered, overallMatchScore: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, campId, OneInput(), default));

        Assert.Contains("đã chốt", ex.Message);
        // job_needs KHÔNG bị đổi.
        Assert.Equal("Thạo .NET", tdb.NewContext().Campaigns.Single(c => c.Id == campId).JobNeeds!.Single().Text);
    }

    // ── (2) 1 row Analyzing (job đang bay, chưa có điểm) ⇒ PUT /job-needs 409 ──────────────────────
    [Fact]
    public async Task Analyzing_row_thi_ReplaceJobNeeds_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraftWithNeeds(tdb, owner);
        await AddCvAsync(tdb, campId, CvSubmissionStatus.Analyzing, overallMatchScore: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, campId, OneInput(), default));

        Assert.Contains("đã chốt", ex.Message);
    }

    // ── (3) Chưa có cv_submission nào ⇒ PUT /job-needs 200 ────────────────────────────────────────
    [Fact]
    public async Task Chua_co_row_nao_thi_ReplaceJobNeeds_200()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraftWithNeeds(tdb, owner);

        var res = await NewSvc(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, campId, OneInput(), default);

        Assert.Single(res.JobNeeds);
        Assert.Equal("Thạo Kafka", res.JobNeeds[0].Text);
    }

    // ── (4) Gửi [] (wipe) khi đã có row ⇒ 409 — [] là replace-all, đi qua đúng cửa CMP4-B1 ────────
    [Fact]
    public async Task Gui_mang_rong_khi_da_co_row_thi_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraftWithNeeds(tdb, owner);
        await AddCvAsync(tdb, campId, CvSubmissionStatus.Filtered, overallMatchScore: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, campId, new List<JobNeedInput>(), default));

        // Không wipe: job_needs cũ còn nguyên.
        Assert.Single(tdb.NewContext().Campaigns.Single(c => c.Id == campId).JobNeeds!);
    }

    // ── (5) Gửi [] khi CHƯA có ai ⇒ wipe được (200, job_needs về null) ────────────────────────────
    // "Wipe khi chưa có ai thì vẫn cho" — đường xoá hợp lệ không bị CMP4-B1 chặn nhầm.
    [Fact]
    public async Task Gui_mang_rong_khi_chua_co_row_thi_wipe_200()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraftWithNeeds(tdb, owner);

        var res = await NewSvc(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, campId, new List<JobNeedInput>(), default);

        Assert.Empty(res.JobNeeds);
        Assert.Null(tdb.NewContext().Campaigns.Single(c => c.Id == campId).JobNeeds);
    }

    // ── (6) Đã có row nhưng ĐÃ CÓ ĐIỂM ⇒ vẫn 409 (không nới sang chiều "cho sửa khi đã có điểm") ──
    // Regression cho hành vi CMP3-B2 đã có: ca "đã sàng" vẫn khoá, chỉ khác là thông điệp không còn
    // nói "được sàng" (thước nay là "bất kỳ cv_submission nào").
    [Fact]
    public async Task Row_da_co_diem_thi_van_409()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campId = SeedDraftWithNeeds(tdb, owner);
        await AddCvAsync(tdb, campId, CvSubmissionStatus.Analyzed, overallMatchScore: 80);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSvc(tdb.NewContext()).ReplaceJobNeedsAsync(owner, owner, campId, OneInput(), default));
    }
}
