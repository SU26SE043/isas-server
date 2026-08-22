using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Wizard tạo lộ trình có bước "chọn lộ trình đã hoàn tất" (gửi <c>priorRoadmapId</c>). Picker đó
/// PHẢI lọc theo <c>hasFinalReport</c>, KHÔNG theo <c>status == Completed</c>:
/// <c>RoadmapService.CreateAsync</c> gác bằng <c>IsNullOrWhiteSpace(prior.FinalReport)</c> → 400,
/// còn <c>RoadmapLessonService.RetryLessonAsync</c> mở lại roadmap <c>Completed → Active</c> và XOÁ
/// <c>FinalReport</c>. Hai vị ngữ gần trùng nhưng KHÔNG đồng nhất.
///
/// <para>Lọc sai ⇒ picker mời một roadmap rồi người dùng ăn 400 SAU KHI đã chờ 13–54 giây tạo
/// roadmap. Cùng lớp bug với picker buổi luyện (xem <c>RoadmapSessionEligibility</c>).</para>
/// </summary>
public class RoadmapHasFinalReportTests
{
    private static RoadmapService Service(TestDb t)
        => new(t.Db, new Mock<IStorageService>().Object,
               new Mock<IAiServiceRoadmapGenerator>().Object, NullLogger<RoadmapService>.Instance);

    private static Roadmap Row(Guid owner, RoadmapStatus status, string? finalReport) => new()
    {
        Id = Guid.NewGuid(),
        CandidateId = owner,
        JobCategory = JobCategory.BE,
        Level = RoadmapLevel.Middle,
        Status = status,
        FinalReport = finalReport,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task CoBaoCao_ThiCoCo_KhongCo_ThiKhong()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var coBaoCao = Row(user, RoadmapStatus.Completed, "{\"overallComment\":\"xong\"}");
        var dangChay = Row(user, RoadmapStatus.Active, null);
        t.Db.AddRange(coBaoCao, dangChay);
        await t.Db.SaveChangesAsync();

        var page = await Service(t).ListAsync(user, null, null);

        Assert.True(page.Items.Single(x => x.Id == coBaoCao.Id).HasFinalReport);
        Assert.False(page.Items.Single(x => x.Id == dangChay.Id).HasFinalReport);
    }

    // 🔑 Ca mà `status == Completed` LỌC SAI: roadmap từng hoàn tất, bị RetryLessonAsync mở lại và
    // xoá report. Nếu picker lọc theo status thì nó vẫn hiện (Completed) — nhưng CreateAsync trả 400.
    [Fact]
    public async Task TungHoanTatNhungBiXoaBaoCao_ThiKHONGCoCo()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var moLai = Row(user, RoadmapStatus.Completed, null);   // status còn Completed, report đã bị xoá
        t.Db.Add(moLai);
        await t.Db.SaveChangesAsync();

        var page = await Service(t).ListAsync(user, null, null);

        Assert.False(Assert.Single(page.Items).HasFinalReport);
    }
}
