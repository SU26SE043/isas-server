using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BK30 — HR đẩy lại sàng CV cho MỘT ứng viên.
///
/// Trước BK30 đây là điểm chết: <c>PublishScreeningJobsAsync</c> lọc cứng <c>Filtered</c>, còn
/// <c>StuckScreeningRepublisher</c> chỉ quét <c>Filtered</c>/<c>Analyzing</c> ⇒ ứng viên đã
/// <c>Analyzed</c> mà thiếu <c>full_name</c> (BK28 chỉ chữa dòng chảy TỚI, không backfill) hoặc
/// đã <c>AnalysisFailed</c> vì quá trần bỏ cuộc thì chỉ còn cách sửa tay trong DB.
///
/// CỐ Ý là đường riêng chứ không nới điều kiện của sweeper: tự động đẩy lại phải khác HR bấm tay.
/// <c>StuckScreeningRepublisherTests.NonPending_Status_NeverRepublished</c> vẫn giữ nguyên và vẫn xanh.
/// </summary>
public class CvRescreenBk30Tests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:CallbackBase"] = "http://campaign:8080" })
            .Build();

    private static CvScreeningService NewService(CampaignDbContext db, ICvScreeningPublisher? publisher = null) =>
        new(db, publisher ?? Mock.Of<ICvScreeningPublisher>(), Config(),
            Mock.Of<ILogger<CvScreeningService>>());

    private static Campaign SeedActiveCampaign(CampaignTestDb tdb, Guid owner)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.Domain = "BE";
        camp.JDText = "JD: cần Backend .NET";
        // Thước đo sàng CV: bộ nhu cầu công việc chốt 1 lần cho cả campaign (chứ không phải
        // campaign_criteria — đó là rubric buổi phỏng vấn).
        camp.JobNeeds = new List<JobNeed>
        {
            new() { NeedId = "need-1", Category = JobNeedCategories.Technical, Text = "Thạo .NET" },
        };
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    private static CampaignCriterion SeedCriterion(CampaignTestDb tdb, Guid campaignId)
    {
        var now = DateTime.UtcNow;
        var c = new CampaignCriterion
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            OrderNo = 0,
            Name = "Tiêu chí 0",
            Description = "mô tả",
            Weight = 1m,
            MaxScore = 5,
            Source = CriterionSource.HrEdited,
            CreatedAt = now,
            UpdatedAt = now
        };
        tdb.Db.CampaignCriteria.Add(c);
        tdb.Db.SaveChanges();
        return c;
    }

    private static CvSubmission SeedCandidate(
        CampaignTestDb tdb, Guid campaignId, CvSubmissionStatus status,
        string? parsedText = "CV text a@x.com", DateTime? lastPublished = null)
    {
        var now = DateTime.UtcNow;
        var cand = new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Email = "a@x.com",
            CvParsedText = parsedText,
            CvFileUrl = $"campaigns/{campaignId}/candidates/x.pdf",
            ParseStatus = CvParseStatus.Done,
            Status = status,
            LastScreeningPublishedAt = lastPublished,
            CreatedAt = now,
            UpdatedAt = now
        };
        tdb.Db.CvSubmissions.Add(cand);
        tdb.Db.SaveChanges();
        return cand;
    }

    // (a) 3 trạng thái được phép → publish job + chuyển Analyzing + đóng dấu marker.
    //     `Analyzed` là ca chính: ứng viên đã chấm xong nhưng thiếu full_name.
    [Theory]
    [InlineData(CvSubmissionStatus.Analyzed)]
    [InlineData(CvSubmissionStatus.AnalysisFailed)]
    [InlineData(CvSubmissionStatus.Filtered)]
    public async Task TrangThaiChoPhep_PublishVaChuyenAnalyzing(CvSubmissionStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var crit = SeedCriterion(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, status);

        var published = new List<CvScreeningJob>();
        var pub = new Mock<ICvScreeningPublisher>();
        pub.Setup(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()))
           .Callback<CvScreeningJob, CancellationToken>((j, _) => published.Add(j))
           .Returns(Task.CompletedTask);

        var svc = NewService(tdb.NewContext(), pub.Object);
        await svc.RescreenCandidateAsync(owner, camp.Id, cand.Id, default);

        var job = Assert.Single(published);
        Assert.Equal(cand.Id, job.CandidateId);
        Assert.Equal("CV text a@x.com", job.CvText);
        Assert.Equal("need-1", Assert.Single(job.JobNeeds).NeedId);
        Assert.Equal("http://campaign:8080", job.CallbackBase);

        using var check = tdb.NewContext();
        var after = await check.CvSubmissions.FirstAsync(c => c.Id == cand.Id);
        Assert.Equal(CvSubmissionStatus.Analyzing, after.Status);
        Assert.NotNull(after.LastScreeningPublishedAt);
    }

    // (b) Trạng thái bị chặn → 409 và TUYỆT ĐỐI không publish (không đốt token).
    //     `Invited` là ca đắt nhất: SaveCvResultAsync bỏ qua Invited nên kết quả sẽ bị vứt ⇒ chạy
    //     tiếp chỉ tổ tốn tiền. `Analyzing` là cooldown chống bấm liên tục.
    [Theory]
    [InlineData(CvSubmissionStatus.Invited)]
    [InlineData(CvSubmissionStatus.Analyzing)]
    [InlineData(CvSubmissionStatus.Rejected)]
    [InlineData(CvSubmissionStatus.Pending)]
    public async Task TrangThaiBiChan_NemConflict_VaKhongPublish(CvSubmissionStatus status)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriterion(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, status);

        var pub = new Mock<ICvScreeningPublisher>();
        var svc = NewService(tdb.NewContext(), pub.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RescreenCandidateAsync(owner, camp.Id, cand.Id, default));

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);

        using var check = tdb.NewContext();
        Assert.Equal(status, (await check.CvSubmissions.FirstAsync(c => c.Id == cand.Id)).Status);
    }

    // (c) CV không có text đọc được → 409, không publish (job rỗng thì AI không có gì để chấm).
    [Fact]
    public async Task CvKhongCoText_NemConflict_KhongPublish()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriterion(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, parsedText: "   ");

        var pub = new Mock<ICvScreeningPublisher>();
        var svc = NewService(tdb.NewContext(), pub.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RescreenCandidateAsync(owner, camp.Id, cand.Id, default));

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (d) Org khác → KeyNotFoundException (controller map 404, không phải 403 — mẫu sẵn có, tránh
    //     lộ sự tồn tại của campaign người khác).
    [Fact]
    public async Task OrgKhac_NemKeyNotFound()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriterion(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed);

        var pub = new Mock<ICvScreeningPublisher>();
        var svc = NewService(tdb.NewContext(), pub.Object);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.RescreenCandidateAsync(Guid.NewGuid(), camp.Id, cand.Id, default));

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (e) Ứng viên thuộc campaign KHÁC → 404 (không đẩy nhầm hồ sơ qua ranh giới campaign).
    [Fact]
    public async Task CandidateCuaCampaignKhac_NemKeyNotFound()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var campA = SeedActiveCampaign(tdb, owner);
        var campB = SeedActiveCampaign(tdb, owner);
        SeedCriterion(tdb, campA.Id);
        var candOfB = SeedCandidate(tdb, campB.Id, CvSubmissionStatus.Analyzed);

        var pub = new Mock<ICvScreeningPublisher>();
        var svc = NewService(tdb.NewContext(), pub.Object);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.RescreenCandidateAsync(owner, campA.Id, candOfB.Id, default));

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (f) Publish ném → KHÔNG đổi trạng thái, HR bấm lại được.
    //     Đổi trạng thái trước rồi publish hụt sẽ đẩy ứng viên vào `Analyzing` mồ côi và phải chờ
    //     hết 15' của sweeper mới được cứu.
    [Fact]
    public async Task PublishNem_GiuNguyenTrangThai()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriterion(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed);

        var pub = new Mock<ICvScreeningPublisher>();
        pub.Setup(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("broker down"));

        var svc = NewService(tdb.NewContext(), pub.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RescreenCandidateAsync(owner, camp.Id, cand.Id, default));

        using var check = tdb.NewContext();
        var after = await check.CvSubmissions.FirstAsync(c => c.Id == cand.Id);
        Assert.Equal(CvSubmissionStatus.Analyzed, after.Status);
        Assert.Null(after.LastScreeningPublishedAt);
    }
}
