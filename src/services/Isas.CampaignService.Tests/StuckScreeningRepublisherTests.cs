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

    // CMP4-B4 — `UpdatedAt` mặc định = `lastPublished ?? createdAt`: mô hình đúng thực tế, đầu lượt
    // (PublishScreeningJobsAsync/RescreenCandidateAsync) ghi CẢ HAI mốc cùng một `now`. Truyền
    // `updatedAt` tường minh cho ca "lượt bắt đầu lâu rồi nhưng republisher vừa đẩy lại" (marker cũ,
    // UpdatedAt mới).
    private static CvSubmission SeedCandidate(
        CampaignTestDb tdb, Guid campaignId, CvSubmissionStatus status,
        DateTime createdAt, DateTime? lastPublished, DateTime? updatedAt = null)
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
            UpdatedAt = updatedAt ?? lastPublished ?? createdAt
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

    // Analyzing vừa được (re)publish 2' trước → worker còn đang chấm → KHÔNG nhặt.
    // CMP4-B4 — nhịp đẩy-lại đo `UpdatedAt` (dời mỗi lần đẩy), KHÔNG `LastScreeningPublishedAt` (mốc
    // bắt đầu lượt, đông cứng): lượt bắt đầu 30' trước NHƯNG lần đẩy gần nhất chỉ 2' trước ⇒ chưa nhặt.
    [Fact]
    public async Task Analyzing_RecentlyPublished_NotRepublished()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriteria(tdb, camp.Id);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddMinutes(-40),
            lastPublished: DateTime.UtcNow.AddMinutes(-30),   // mốc bắt đầu lượt: 30' trước (nếu nhịp đo đây ⇒ bị nhặt oan)
            updatedAt: DateTime.UtcNow.AddMinutes(-2));        // lần đẩy gần nhất: 2' trước ⇒ để worker chấm tiếp

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Analyzing, lần ghi gần nhất 20' trước, không callback → worker mất tích → re-publish.
    // CMP4-B4 — republisher dời `UpdatedAt` (nhịp đẩy-lại) chứ KHÔNG dời `LastScreeningPublishedAt`
    // (mốc bắt đầu lượt — phải đông cứng để trần bỏ cuộc có điểm tới).
    [Fact]
    public async Task Analyzing_LostLongAgo_IsRepublished()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriteria(tdb, camp.Id);
        var roundStart = DateTime.UtcNow.AddMinutes(-20);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddMinutes(-40), lastPublished: roundStart);

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Once);
        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.True(saved.UpdatedAt > DateTime.UtcNow.AddMinutes(-1));                    // nhịp đẩy-lại dời sang now
        Assert.NotNull(saved.LastScreeningPublishedAt);
        Assert.True(saved.LastScreeningPublishedAt < DateTime.UtcNow.AddMinutes(-10));    // mốc bắt đầu lượt ĐÔNG CỨNG (~20' trước, KHÔNG dời)
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

    // CMP4-B4 — trần neo vào MỐC BẮT ĐẦU LƯỢT (LastScreeningPublishedAt), không phải upload.
    // Lượt bắt đầu 7 giờ trước (> trần mặc định 6h), republisher đã đẩy lại, lần ghi gần nhất 20' trước.
    [Fact]
    public async Task QuaTranBoCuoc_ChuyenAnalysisFailed_VaKhongDayLai()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddHours(-8),
            lastPublished: DateTime.UtcNow.AddHours(-7),          // lượt bắt đầu 7h trước — quá trần 6h
            updatedAt: DateTime.UtcNow.AddMinutes(-20));          // republisher vừa đẩy lại 20' trước

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.Equal(CvSubmissionStatus.AnalysisFailed, saved.Status);
        Assert.NotNull(saved.RejectReason);   // HR phải NHÌN THẤY lý do, không im lặng kẹt Analyzing
    }

    // CMP4-B4 — ĐỔI TIỀN ĐỀ CÓ CHỦ ĐÍCH (ca cũ `QuaTranBoCuoc_TinhTheoCreatedAt_DuMarkerVuaDoi`
    // khoá đúng lỗi CMP4-B4 sửa): trước đây trần neo vào `CreatedAt` nên "ứng viên CŨ, marker vừa
    // dời ⇒ vẫn bỏ cuộc". Nay trần neo vào MỐC BẮT ĐẦU LƯỢT: hồ sơ tạo 30h trước nhưng lượt đánh
    // giá (rescreen) chỉ mới bắt đầu 16' trước ⇒ KHÔNG bỏ cuộc oan, worker mới chạy 16'.
    [Fact]
    public async Task QuaTranBoCuoc_TinhTheoMocBatDauLuot_KhongTheoCreatedAt()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddHours(-30),
            lastPublished: DateTime.UtcNow.AddMinutes(-20),       // lượt vừa bắt đầu — KHÔNG quá trần
            updatedAt: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(CvSubmissionStatus.Analyzing,
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

    // ── CMP4-B1 — kiểm THƯỚC ĐO (job_needs) TRƯỚC trần bỏ cuộc ──────────────────────────────────────
    //
    // Bug đã đo trên dev: campaign CHƯA chốt job_needs (lỗi cấu hình) + row còn Filtered → trần bỏ
    // cuộc chạy TRƯỚC phép kiểm job_needs ⇒ 6h sau row bị lật AnalysisFailed với lý do "worker sàng
    // CV không phản hồi", đổ oan cho worker cho một chuyện worker không dính. "Lời nói dối 6 giờ" mà
    // CMP3-B1 tuyên bố đã diệt VẪN tới được qua cửa này.

    private static Campaign SeedActiveCampaignNoNeeds(CampaignTestDb tdb, Guid owner)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.Domain = "BE";
        camp.JDText = "JD: cần Backend .NET";
        camp.JobNeeds = null;   // CHƯA chốt nhu cầu công việc — lỗi cấu hình
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    // job_needs rỗng + quá 6h ⇒ VẪN lật AnalysisFailed (HR nhìn thấy) NHƯNG lý do là "chưa chốt
    // nhu cầu công việc", KHÔNG phải "worker không phản hồi". Không publish (không có thước để gửi).
    [Fact]
    public async Task JobNeedsRong_QuaTranBoCuoc_KHONG_DoOanWorker()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaignNoNeeds(tdb, Guid.NewGuid());
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered,
            createdAt: DateTime.UtcNow.AddHours(-7), lastPublished: null);

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);

        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.Equal(CvSubmissionStatus.AnalysisFailed, saved.Status);
        Assert.NotNull(saved.RejectReason);
        Assert.Contains("nhu cầu công việc", saved.RejectReason!);
        Assert.DoesNotContain("worker", saved.RejectReason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("không phản hồi", saved.RejectReason!);
    }

    // job_needs rỗng NHƯNG chưa quá trần ⇒ chỉ bỏ qua (log warning), KHÔNG lật trạng thái.
    [Fact]
    public async Task JobNeedsRong_ChuaQuaTran_ChiBoQua_KhongLat()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaignNoNeeds(tdb, Guid.NewGuid());
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered,
            createdAt: DateTime.UtcNow.AddMinutes(-10), lastPublished: null);

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(CvSubmissionStatus.Filtered,
            (await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id)).Status);
    }

    // Regression: CÓ job_needs + lượt bắt đầu quá trần ⇒ lý do vẫn là "worker sàng CV không phản
    // hồi" (ca worker thật sự mất tích — phép kiểm job_needs đứng TRƯỚC nhưng không nuốt ca này).
    // CMP4-B4 — `lastPublished` = MỐC BẮT ĐẦU LƯỢT (7h trước, quá trần); `updatedAt` = lần đẩy gần nhất.
    [Fact]
    public async Task CoJobNeeds_QuaTranBoCuoc_VanBao_WorkerKhongPhanHoi()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());   // helper này LUÔN set job_needs
        SeedCriteria(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddHours(-8),
            lastPublished: DateTime.UtcNow.AddHours(-7),
            updatedAt: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.Equal(CvSubmissionStatus.AnalysisFailed, saved.Status);
        Assert.Contains("worker sàng CV không phản hồi", saved.RejectReason!);
    }

    // ── CMP4-B4 — TRẦN BỎ CUỘC neo vào MỐC BẮT ĐẦU LƯỢT ĐÁNH GIÁ, không phải mốc upload ────────────
    //
    // BỐI CẢNH: trần neo vào `CreatedAt`. CMP3-B2 cho cv_submission sống nhiều ngày trong Draft trước
    // khi campaign chạy ⇒ hồ sơ tạo 3 ngày trước, HR bấm rescreen (đặt Analyzing + mốc bắt đầu lượt =
    // now) → 15' sau sweeper nhặt → `CreatedAt` đã quá 6h → AnalysisFailed NGAY, kèm "worker không
    // phản hồi" trong khi worker mới chỉ chạy 15'. CMP4-B4 neo trần vào `LastScreeningPublishedAt ??
    // CreatedAt` = mốc lượt bắt đầu; republisher chỉ dời `UpdatedAt` (nhịp đẩy-lại), KHÔNG dời mốc này.

    // (1) Hồ sơ tạo 3 ngày trước, lượt đánh giá VỪA bắt đầu (rescreen 20' trước) ⇒ KHÔNG lật oan.
    [Fact]
    public async Task Cmp4B4_HoSoCu_VuaRescreen_KHONG_LatOan()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddDays(-3),
            lastPublished: DateTime.UtcNow.AddMinutes(-20),   // rescreen 20' trước = mốc bắt đầu lượt
            updatedAt: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Once);
        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.Equal(CvSubmissionStatus.Analyzing, saved.Status);   // KHÔNG AnalysisFailed
        Assert.Null(saved.RejectReason);
    }

    // (2) Lượt đánh giá bắt đầu 7h trước (> trần 6h), republisher đã đẩy lại (UpdatedAt 20' trước)
    //     ⇒ CÓ lật, lý do đúng nguyên nhân "worker sàng CV không phản hồi".
    [Fact]
    public async Task Cmp4B4_LuotDanhGiaQuaTran_CO_Lat_LyDoDungWorker()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddHours(-9),
            lastPublished: DateTime.UtcNow.AddHours(-7),
            updatedAt: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Never);
        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.Equal(CvSubmissionStatus.AnalysisFailed, saved.Status);
        Assert.Contains("worker sàng CV không phản hồi", saved.RejectReason!);
        Assert.DoesNotContain("nhu cầu công việc", saved.RejectReason!);   // KHÔNG phải lý do cấu hình (CMP4-B1)
    }

    // (3) DISCRIMINATOR — hai hồ sơ CÙNG `CreatedAt` rất cũ (-9h), khác `LastScreeningPublishedAt`:
    //     lượt bắt đầu 7h trước ⇒ lật; lượt bắt đầu 1h trước ⇒ KHÔNG. Chứng minh neo vào mốc-bắt-đầu-
    //     lượt, KHÔNG phải `CreatedAt`.
    [Fact]
    public async Task Cmp4B4_TranNeoVaoMocBatDauLuot_KhongPhaiCreatedAt()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        var ancient = DateTime.UtcNow.AddHours(-9);
        var stale = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: ancient, lastPublished: DateTime.UtcNow.AddHours(-7),
            updatedAt: DateTime.UtcNow.AddMinutes(-20));
        var fresh = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: ancient, lastPublished: DateTime.UtcNow.AddHours(-1),
            updatedAt: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        using var check = tdb.NewContext();
        Assert.Equal(CvSubmissionStatus.AnalysisFailed,
            (await check.CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == stale.Id)).Status);
        Assert.Equal(CvSubmissionStatus.Analyzing,
            (await check.CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == fresh.Id)).Status);
    }

    // (4) MECHANISM — republisher đẩy lại một lượt CHƯA quá trần: `LastScreeningPublishedAt` (mốc bắt
    //     đầu lượt) ĐÔNG CỨNG, chỉ `UpdatedAt` dời sang now. Nếu mốc này bị dời thì trần không bao giờ tới.
    [Fact]
    public async Task Cmp4B4_RepublisherKhongDoiMocBatDauLuot()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedActiveCampaign(tdb, Guid.NewGuid());
        SeedCriteria(tdb, camp.Id);
        var roundStart = DateTime.UtcNow.AddHours(-2);   // lượt bắt đầu 2h trước, chưa quá trần 6h
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing,
            createdAt: DateTime.UtcNow.AddHours(-3),
            lastPublished: roundStart, updatedAt: DateTime.UtcNow.AddMinutes(-20));

        var (r, pub) = Build(tdb);
        await ScanOnce(r);

        pub.Verify(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()), Times.Once);
        var saved = await tdb.NewContext().CvSubmissions.AsNoTracking().FirstAsync(x => x.Id == cand.Id);
        Assert.True(saved.UpdatedAt > DateTime.UtcNow.AddMinutes(-1));                   // nhịp đẩy-lại dời sang now
        Assert.True(saved.LastScreeningPublishedAt < DateTime.UtcNow.AddHours(-1));      // mốc bắt đầu lượt ĐÔNG CỨNG (~2h trước)
    }
}
