using System.Reflection;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// C15 — StuckScreeningRepublisher: quét CV sàng kẹt → đẩy lại cv_screening_queue.
/// • Filtered + last_published=null quá 2' (publish hụt) → re-publish + Analyzing + set last_published;
/// • Analyzing quá 15' không callback (worker mất tích) → re-publish;
/// • fresh Filtered (còn grace) / Analyzing mới publish / Analyzed / Rejected / Invited → KHÔNG nhặt.
/// Gọi ScanOnceAsync trực tiếp (KHÔNG chạy timer); publisher mock; ServiceProvider thật (scope DbContext).
/// </summary>
public class StuckScreeningRepublisherTests
{
    // Gọi ScanOnceAsync (private) một nhịp.
    private static async Task ScanOnce(StuckScreeningRepublisher r)
    {
        var mi = typeof(StuckScreeningRepublisher)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    // ServiceProvider thật để CreateScope() trả về CampaignDbContext dùng chung connection SQLite.
    private static (StuckScreeningRepublisher r, Mock<ICvScreeningPublisher> pub) Build(
        CampaignTestDb t, string? giveUpHours = null)
    {
        var services = new ServiceCollection();
        // DB2b — khớp snake_case schema do CampaignTestDb EnsureCreated (partial index outbox_messages).
        services.AddDbContext<CampaignDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var settings = new Dictionary<string, string?> { ["Internal:CallbackBase"] = "http://campaign:8080" };
        if (giveUpHours is not null) settings["Screening:GiveUpAfterHours"] = giveUpHours;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var pub = new Mock<ICvScreeningPublisher>();
        var r = new StuckScreeningRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            pub.Object,
            config,
            NullLogger<StuckScreeningRepublisher>.Instance);
        return (r, pub);
    }

    private static Campaign SeedActiveCampaign(CampaignTestDb tdb, Guid owner)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.Domain = "BE";
        camp.JDText = "JD: cần Backend .NET";
        // Thước đo sàng CV: bộ nhu cầu công việc chốt 1 lần cho cả campaign.
        camp.JobNeeds = new List<JobNeed>
        {
            new() { NeedId = "need-1", Category = JobNeedCategories.Technical, Text = "Thạo .NET" },
            new() { NeedId = "need-2", Category = JobNeedCategories.Communication, Text = "Trao đổi với khách" },
        };
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    private static void SeedCriteria(CampaignTestDb tdb, Guid campaignId, int count = 1)
    {
        var now = DateTime.UtcNow;
        tdb.Db.CampaignCriteria.AddRange(Enumerable.Range(0, count).Select(i => new CampaignCriterion
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            OrderNo = i,
            Name = $"Tiêu chí {i}",
            Description = $"mô tả {i}",
            Weight = Math.Round(1m / count, 4),
            MaxScore = 5,
            Source = CriterionSource.HrEdited,
            CreatedAt = now,
            UpdatedAt = now
        }));
        tdb.Db.SaveChanges();
    }

    private static CvSubmission SeedCandidate(
        CampaignTestDb tdb, Guid campaignId, CvSubmissionStatus status,
        DateTime createdAt, DateTime? lastPublished)
    {
        var cand = new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Email = $"{Guid.NewGuid():N}@x.com",
            CvParsedText = "CV text a@x.com",
            CvFileUrl = $"campaigns/{campaignId}/candidates/x.pdf",
            ParseStatus = CvParseStatus.Done,
            Status = status,
            LastScreeningPublishedAt = lastPublished,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        tdb.Db.CvSubmissions.Add(cand);
        tdb.Db.SaveChanges();
        return cand;
    }

    // Publish hụt: Filtered, chưa publish (null), tạo 10' trước → re-publish + Analyzing + set marker.
    [Fact]
    public async Task PublishHut_Filtered_NullMarker_Old_Republished_And_Analyzing()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriteria(tdb, camp.Id, 2);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered,
            createdAt: DateTime.UtcNow.AddMinutes(-10), lastPublished: null);

        var (r, pub) = Build(tdb);
        CvScreeningJob? published = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()))
           .Callback<CvScreeningJob, CancellationToken>((j, _) => published = j)
           .Returns(Task.CompletedTask);

        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(published);
        Assert.Equal(cand.Id, published!.CandidateId);
        Assert.Equal(2, published.JobNeeds.Count);                    // thước đo = job_needs của campaign
        Assert.Equal("http://campaign:8080", published.CallbackBase);

        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.Equal(CvSubmissionStatus.Analyzing, saved.Status);        // Filtered → Analyzing
        Assert.NotNull(saved.LastScreeningPublishedAt);               // marker dời sang now
    }

    // Filtered vừa tạo (30s trước, còn grace 2') → request sàng có thể đang chạy dở → KHÔNG nhặt.
    [Fact]
    public async Task FreshFiltered_WithinGrace_NotRepublished()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriteria(tdb, camp.Id);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered,
            createdAt: DateTime.UtcNow.AddSeconds(-30), lastPublished: null);

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Analyzing mới publish 2' trước → worker còn đang chấm → KHÔNG nhặt.
    [Fact]
    public async Task Analyzing_RecentlyPublished_NotRepublished()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriteria(tdb, camp.Id);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddMinutes(-20), lastPublished: DateTime.UtcNow.AddMinutes(-2));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Analyzing publish 20' trước, không callback → worker mất tích → re-publish + dời marker.
    [Fact]
    public async Task Analyzing_LostLongAgo_IsRepublished()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriteria(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddMinutes(-40), lastPublished: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Once);
        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.True(saved.LastScreeningPublishedAt > DateTime.UtcNow.AddMinutes(-1));   // marker dời sang now
    }

    // Analyzed / Rejected / Invited → terminal/chờ HR → KHÔNG nhặt (dù cũ).
    [Theory]
    [InlineData(CvSubmissionStatus.Analyzed)]
    [InlineData(CvSubmissionStatus.Rejected)]
    [InlineData(CvSubmissionStatus.Invited)]
    [InlineData(CvSubmissionStatus.AnalysisFailed)]
    public async Task NonPending_Status_NeverRepublished(CvSubmissionStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriteria(tdb, camp.Id);
        SeedCandidate(tdb, camp.Id, status,
            createdAt: DateTime.UtcNow.AddMinutes(-60), lastPublished: DateTime.UtcNow.AddMinutes(-50));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Campaign soft-deleted → query filter loại candidate của nó (không re-publish CV campaign đã xoá).
    [Fact]
    public async Task DeletedCampaign_Candidate_NotRepublished()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriteria(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered,
            createdAt: DateTime.UtcNow.AddMinutes(-10), lastPublished: null);
        // soft-delete campaign
        var c = await tdb.Db.Campaigns.FirstAsync(x => x.Id == camp.Id);
        c.DeletedAt = DateTime.UtcNow;
        await tdb.Db.SaveChangesAsync();

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── TRẦN BỎ CUỘC ────────────────────────────────────────────────────────────────────────────
    //
    // Không có trần thì vòng đẩy-lại KHÔNG có điểm dừng. Đo trên production 2026-08-02:
    // `cv_screening_queue` tồn **713 message của đúng 8 ứng viên** — consumer chưa từng tồn tại nên
    // mỗi người bị đẩy lại 1 lần/15' suốt ~22 tiếng, và không alert nào đọc `list_queues` để ai biết.
    // Mỗi bản nhân đôi = 1 lượt Gemini nếu consumer bật lên.

    [Fact]
    public async Task QuaTranBoCuoc_ChuyenAnalysisFailed_VaKhongDayLai()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        // Tạo 7 giờ trước (> trần mặc định 6h), vẫn Analyzing và đã quá 15' không callback.
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddHours(-7), lastPublished: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.Equal(CvSubmissionStatus.AnalysisFailed, saved.Status);
        Assert.NotNull(saved.RejectReason);   // HR phải NHÌN THẤY lý do, không im lặng kẹt Analyzing
    }

    // Neo trần theo `CreatedAt`, KHÔNG theo `LastScreeningPublishedAt`: mốc sau bị chính vòng lặp này
    // dời về `now` mỗi lần đẩy ⇒ lấy nó làm trần thì trần không bao giờ tới. Ca này khoá đúng điều đó:
    // ứng viên CŨ nhưng marker vừa mới dời vẫn phải bị bỏ cuộc.
    [Fact]
    public async Task QuaTranBoCuoc_TinhTheoCreatedAt_DuMarkerVuaDoi()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddHours(-30), lastPublished: DateTime.UtcNow.AddMinutes(-16));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(CvSubmissionStatus.AnalysisFailed,
            (await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id)).Status);
    }

    [Fact]
    public async Task ChuaQuaTranBoCuoc_VanDayLaiBinhThuong()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddHours(-1), lastPublished: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(CvSubmissionStatus.Analyzing,
            (await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id)).Status);
    }

    // `Screening:GiveUpAfterHours = 0` = TẮT trần (đẩy lại vô hạn như trước). Có công tắc để ai đó
    // cố ý chọn hành vi cũ, nhưng phải là lựa chọn tường minh chứ không phải mặc định.
    [Fact]
    public async Task TranBang0_TatTran_VanDayLaiDuRatCu()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddDays(-30), lastPublished: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb, giveUpHours: "0");
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
