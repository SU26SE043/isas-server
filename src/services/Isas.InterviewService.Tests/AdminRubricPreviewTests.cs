using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// CHẤM THỬ bộ chuẩn B2C (admin) — AI viết 3 bài mẫu rồi chấm chính chúng bằng thước đo đang lưu.
///
/// <para>Điểm sống còn của cả tính năng: thứ admin kiểm chứng phải LÀ thứ người luyện bị chấm. Hai
/// đường trôi khỏi nhau thì cả hai vẫn ra điểm — chỉ là điểm của hai thước đo khác nhau, không triệu
/// chứng nào.</para>
/// </summary>
public class AdminRubricPreviewTests
{
    private static readonly List<AdminRubricLevelInput> Levels =
    [
        new(0, "Không nêu được ý nào liên quan tới câu hỏi, hoặc bỏ trống."),
        new(3, "Nêu được ý chính nhưng thiếu ví dụ cụ thể và chưa nói tới đánh đổi."),
        new(5, "Nêu ý chính, có ví dụ từ dự án thật và chỉ ra được đánh đổi của phương án.")
    ];

    /// <summary>Bộ chuẩn (nghề, ngôn ngữ) ĐÃ khai mốc — điều kiện để chấm thử chạy được.</summary>
    private static async Task<int> SeedRubricWithLevelsAsync(
        TestDb t, JobCategory cat = JobCategory.BE, string lang = "vi")
    {
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();

        var admin = new AdminB2CRubricService(t.Db);
        var v1 = (await admin.GetAsync(cat, lang))!;
        var v2 = (await admin.ReplaceAsync(cat, new UpsertAdminRubricRequest(
            v1.Criteria.Select(c => new AdminRubricCriterionInput(c.Id, c.Description, Levels)).ToList()),
            lang))!;
        return v2.Version;
    }

    private static Mock<IRubricPreviewClient> AiMock(decimal weak = 1m, decimal good = 3m, decimal top = 5m)
    {
        var mock = new Mock<IRubricPreviewClient>();
        mock.Setup(m => m.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<PreviewCriterionInput>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string? _, string _, string? _, string? _, int _,
                IReadOnlyList<PreviewCriterionInput> criteria, CancellationToken _) =>
                new RubricPreviewResult(
                    [
                        Sample("Weak", criteria, weak),
                        Sample("Good", criteria, good),
                        Sample("Excellent", criteria, top)
                    ],
                    PromptVersion: 7, LengthParityWarning: false));
        return mock;
    }

    private static PreviewSample Sample(string band, IReadOnlyList<PreviewCriterionInput> criteria, decimal score)
        => new(band, $"bài {band}", 160,
            criteria.Select(c => new PreviewSampleScore(c.CriterionId, score, (int)score, "vì thế")).ToList());

    private static AdminRubricPreviewService Service(
        TestDb t, IRubricPreviewClient? ai = null, IAiServiceLevelSuggester? suggester = null)
        => new(t.Db, ai ?? AiMock().Object, suggester, NullLogger<AdminRubricPreviewService>.Instance);

    // ── (1) Đường chấm thử phải LÀ đường chấm thật ───────────────────────────────────────────

    /// <summary>
    /// Mảng <c>levels</c> gửi đi chấm thử phải khớp BYTE với thứ
    /// <see cref="ScoringCriteriaBuilder"/> sinh ra cho đường chấm thật.
    ///
    /// Đây là bất biến mà cả tính năng đứng lên: dựng lại mảng ở đây (tự sort, tự map) là mở đúng cái
    /// khe cho hai đường trôi xa nhau mà KHÔNG có triệu chứng nào.
    /// </summary>
    [Fact]
    public async Task PreviewCriteria_ComeFromScoringCriteriaBuilder()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        var criteria = await t.Db.RubricCriteria.AsNoTracking().Include(c => c.Levels)
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == JobCategory.BE && c.Language == "vi" && c.IsActive)
            .OrderBy(c => c.Name).ToListAsync();

        var real = ScoringCriteriaBuilder.Build(criteria);
        var forPreview = AdminRubricPreviewService.BuildPreviewCriteria(criteria);

        Assert.Equal(real.Count, forPreview.Count);
        foreach (var r in real)
        {
            var p = forPreview.Single(x => x.CriterionId == r.CriterionId);
            Assert.Equal(r.MaxScore, p.MaxScore);
            Assert.Equal(r.Weight, p.Weight);
            Assert.Equal(
                r.Levels.Select(l => (l.Score, l.Descriptor)),
                p.Levels.Select(l => (l.Score, l.Descriptor)));
        }
    }

    /// <summary>
    /// Mức kỳ vọng do CODE chọn, qua <see cref="ExpectedLevels"/> dùng chung với đường của employer —
    /// không phải model tự đặt, và không phải một phép chọn riêng của Interview.
    /// </summary>
    [Fact]
    public async Task ExpectedLevels_ComeFromSharedRule()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        var criteria = await t.Db.RubricCriteria.AsNoTracking().Include(c => c.Levels)
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == JobCategory.BE && c.Language == "vi" && c.IsActive)
            .ToListAsync();

        var p = AdminRubricPreviewService.BuildPreviewCriteria(criteria)[0];
        var expected = Isas.Shared.Rubric.ExpectedLevels.For(
            [new(0, "x"), new(3, "y"), new(5, "z")]);

        Assert.Equal(expected.Weak, p.ExpectedWeak);
        Assert.Equal(expected.Good, p.ExpectedGood);
        Assert.Equal(expected.Excellent, p.ExpectedExcellent);
    }

    // ── (2) Quota — miễn phí, trần cứng, chỉ đếm Succeeded ───────────────────────────────────

    [Fact]
    public async Task Run_BeyondFreeQuota_Throws429Shaped()
    {
        using var t = new TestDb();
        var version = await SeedRubricWithLevelsAsync(t);
        var svc = Service(t);

        for (var i = 0; i < AdminRubricPreviewService.FreeRunsPerRubricVersion; i++)
            await svc.RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());

        await Assert.ThrowsAsync<PreviewQuotaExceededException>(
            () => svc.RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest()));

        Assert.Equal(AdminRubricPreviewService.FreeRunsPerRubricVersion,
            await t.Db.AdminRubricPreviewRuns.CountAsync(
                r => r.RubricVersion == version && r.Status == AdminRubricPreviewStatus.Succeeded));
    }

    /// <summary>
    /// Lượt HỎNG không ăn quota. Phạt người soạn vì AI của ta hỏng là sai — và nếu tính thì ba lần
    /// Gemini trục trặc là admin mất sạch lượt của phiên bản đó.
    /// </summary>
    [Fact]
    public async Task Run_FailedRuns_DoNotConsumeQuota()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);

        var broken = new Mock<IRubricPreviewClient>();
        broken.Setup(m => m.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<PreviewCriterionInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamServiceException("AIService sập"));

        var failing = Service(t, broken.Object);
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<DownstreamServiceException>(
                () => failing.RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest()));

        // Vẫn còn nguyên quota.
        var ok = await Service(t).RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());
        Assert.Equal(AdminRubricPreviewService.FreeRunsPerRubricVersion - 1, ok.FreeRunsRemaining);
    }

    /// <summary>Lưu bản mốc mới ⇒ phiên bản mới ⇒ quota mới. Thước đo mới là bài toán mới.</summary>
    [Fact]
    public async Task Run_QuotaResetsPerRubricVersion()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        var svc = Service(t);
        for (var i = 0; i < AdminRubricPreviewService.FreeRunsPerRubricVersion; i++)
            await svc.RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());

        // Sửa một mô tả ⇒ bump phiên bản.
        var admin = new AdminB2CRubricService(t.Db);
        var cur = (await admin.GetAsync(JobCategory.BE, "vi"))!;
        await admin.ReplaceAsync(JobCategory.BE, new UpsertAdminRubricRequest(
            cur.Criteria.Select(c => new AdminRubricCriterionInput(
                c.Id, c.Description + " (v2)",
                c.Levels.Select(l => new AdminRubricLevelInput(l.Score, l.Descriptor)).ToList())).ToList()),
            "vi");

        var run = await svc.RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());
        Assert.Equal(AdminRubricPreviewService.FreeRunsPerRubricVersion - 1, run.FreeRunsRemaining);
    }

    // ── (3) Row Running ghi TRƯỚC khi gọi AI ────────────────────────────────────────────────

    /// <summary>
    /// AI lỗi ⇒ row vẫn còn, ở trạng thái <c>Failed</c> kèm lý do. Ghi row trước có chủ đích: nó vừa
    /// là khoá chống double-click vừa là chỗ kết quả rơi vào kể cả khi trình duyệt admin chết.
    /// </summary>
    [Fact]
    public async Task Run_AiFails_LeavesFailedRowWithReason()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        var broken = new Mock<IRubricPreviewClient>();
        broken.Setup(m => m.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<PreviewCriterionInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamServiceException("AIService sập"));

        await Assert.ThrowsAsync<DownstreamServiceException>(
            () => Service(t, broken.Object).RunAsync(
                Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest()));

        var row = await t.Db.AdminRubricPreviewRuns.AsNoTracking().SingleAsync();
        Assert.Equal(AdminRubricPreviewStatus.Failed, row.Status);
        Assert.Contains("AIService", row.ErrorReason);
        Assert.NotNull(row.CompletedAt);
    }

    /// <summary>Row <c>Running</c> mồ côi quá 5 phút phải tự lành, nếu không phạm vi đó kẹt 409 mãi.</summary>
    [Fact]
    public async Task Run_StaleRunningRow_SelfHeals()
    {
        using var t = new TestDb();
        var version = await SeedRubricWithLevelsAsync(t);
        t.Db.AdminRubricPreviewRuns.Add(new AdminRubricPreviewRun
        {
            JobCategory = JobCategory.BE,
            Language = "vi",
            RubricVersion = version,
            CreatedByUserId = Guid.NewGuid(),
            QuestionText = "câu cũ",
            Status = AdminRubricPreviewStatus.Running,
            RubricSnapshot = "[]",
            RubricFingerprint = new string('0', 64),
            CreatedAt = DateTime.UtcNow.AddMinutes(-30)
        });
        await t.Db.SaveChangesAsync();

        var run = await Service(t).RunAsync(
            Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());

        Assert.Equal(nameof(AdminRubricPreviewStatus.Succeeded), run.Status);
        Assert.Equal(1, await t.Db.AdminRubricPreviewRuns.CountAsync(
            r => r.Status == AdminRubricPreviewStatus.Failed));
    }

    /// <summary>Lượt đang chạy (chưa quá hạn) ⇒ 409, không chạy song song.</summary>
    [Fact]
    public async Task Run_WhileAnotherRunning_Throws()
    {
        using var t = new TestDb();
        var version = await SeedRubricWithLevelsAsync(t);
        t.Db.AdminRubricPreviewRuns.Add(new AdminRubricPreviewRun
        {
            JobCategory = JobCategory.BE,
            Language = "vi",
            RubricVersion = version,
            CreatedByUserId = Guid.NewGuid(),
            QuestionText = "đang chạy",
            Status = AdminRubricPreviewStatus.Running,
            RubricSnapshot = "[]",
            RubricFingerprint = new string('0', 64),
            CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(t).RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest()));
        Assert.Contains("Đang có một lượt", ex.Message);
    }

    // ── (4) Điều kiện đầu vào ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Chưa khai mốc ⇒ từ chối. Chấm thử trên dải mặc định chỉ đang kiểm chứng chính dải mặc định —
    /// tức đốt một lượt AI để xác nhận đúng hiện trạng mà ta đang tìm cách thoát ra.
    /// </summary>
    [Fact]
    public async Task Run_WithoutLevels_Throws()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(t).RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest()));
        Assert.Contains("Chưa khai mốc", ex.Message);
    }

    [Fact]
    public async Task Run_WithoutRubric_Throws404Shaped()
    {
        using var t = new TestDb();
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Service(t).RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest()));
    }

    /// <summary>
    /// Câu hỏi mặc định đến từ bộ mẫu HẰNG SỐ trong code, KHÔNG từ <c>practice_questions</c> thật —
    /// câu thật sinh từ CV/JD của người dùng nên chứa tên công ty/dự án của họ.
    /// </summary>
    [Fact]
    public async Task Run_DefaultQuestion_ComesFromConstantBank_NotRealSessions()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        // Một câu hỏi THẬT có nội dung nhạy cảm, đang nằm trong DB.
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        var question = TestDb.Question(session.Id);
        question.Content = "Ở dự án ACME Bank bạn đã tối ưu truy vấn báo cáo thế nào?";
        t.Db.AddRange(session, question);
        await t.Db.SaveChangesAsync();

        var run = await Service(t).RunAsync(
            Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());

        Assert.DoesNotContain("ACME", run.QuestionText);
        Assert.Contains(AdminPreviewQuestionBank.For(JobCategory.BE, "vi"), q => q.Text == run.QuestionText);
    }

    // ── (4b) Câu mẫu đi qua API — MỘT nguồn sự thật ─────────────────────────────────────────

    /// <summary>
    /// <c>GET</c> bộ chuẩn trả kèm danh sách câu mẫu của đúng (nghề, ngôn ngữ) đó.
    ///
    /// Không có vế này thì giao diện phải tự chép nội dung câu mẫu vào code của nó ⇒ hai nguồn sự
    /// thật: sửa câu ở backend thì màn hình vẫn hiện câu cũ, và không gì báo.
    /// </summary>
    [Fact]
    public async Task AdminRubricGet_CarriesSampleQuestions_ScopedToCategoryAndLanguage()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();
        var admin = new AdminB2CRubricService(t.Db);

        var beVi = (await admin.GetAsync(JobCategory.BE, "vi"))!;
        var beEn = (await admin.GetAsync(JobCategory.BE, "en"))!;
        var faVi = (await admin.GetAsync(JobCategory.BA, "vi"))!;

        Assert.Equal(3, beVi.SampleQuestions.Count);
        Assert.All(beVi.SampleQuestions, q => Assert.StartsWith("BE-vi-", q.Id));
        Assert.All(beVi.SampleQuestions, q => Assert.False(string.IsNullOrWhiteSpace(q.Text)));
        // Không lẫn tổ hợp khác.
        Assert.Empty(beVi.SampleQuestions.Select(q => q.Text).Intersect(beEn.SampleQuestions.Select(q => q.Text)));
        Assert.Empty(beVi.SampleQuestions.Select(q => q.Text).Intersect(faVi.SampleQuestions.Select(q => q.Text)));
    }

    /// <summary>Chọn câu bằng id ⇒ chấm thử dùng ĐÚNG câu đó, không phải câu đầu danh sách.</summary>
    [Fact]
    public async Task Run_WithSampleQuestionId_UsesThatExactQuestion()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        var third = AdminPreviewQuestionBank.For(JobCategory.BE, "vi")[2];

        var run = await Service(t).RunAsync(Guid.NewGuid(), JobCategory.BE, "vi",
            new AdminRubricPreviewRequest(SampleQuestionId: third.Id));

        Assert.Equal(third.Text, run.QuestionText);
    }

    /// <summary>
    /// Id bịa ⇒ 400 nêu rõ, KHÔNG âm thầm rơi về câu mặc định. Rơi âm thầm thì admin tưởng mình đang
    /// kiểm chứng câu A trong khi hệ thống chấm câu B — và báo cáo trông vẫn hợp lý.
    /// </summary>
    [Fact]
    public async Task Run_WithUnknownSampleQuestionId_Throws_DoesNotFallBack()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(t).RunAsync(Guid.NewGuid(), JobCategory.BE, "vi",
                new AdminRubricPreviewRequest(SampleQuestionId: "BE-vi-99")));

        Assert.Contains("BE-vi-99", ex.Message);
        Assert.Empty(await t.Db.AdminRubricPreviewRuns.ToListAsync());   // không tạo lượt nào
    }

    /// <summary>Id của tổ hợp KHÁC (đúng dạng nhưng sai nghề) cũng phải bị từ chối.</summary>
    [Fact]
    public async Task Run_WithSampleQuestionIdFromOtherCategory_Throws()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        var otherCategory = AdminPreviewQuestionBank.For(JobCategory.FE, "vi")[0];

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(t).RunAsync(Guid.NewGuid(), JobCategory.BE, "vi",
                new AdminRubricPreviewRequest(SampleQuestionId: otherCategory.Id)));
    }

    [Fact]
    public async Task Run_CustomQuestion_IsUsed()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        var run = await Service(t).RunAsync(Guid.NewGuid(), JobCategory.BE, "vi",
            new AdminRubricPreviewRequest(Question: "  Câu tự gõ  "));
        Assert.Equal("Câu tự gõ", run.QuestionText);
    }

    // ── (5) Báo cáo — công thức phải khớp cách người luyện thật được chấm ────────────────────

    /// <summary>
    /// Điểm quy % dùng TRUNG BÌNH CỘNG (INT-10), KHÔNG weighted như B2B.
    ///
    /// Seed B2C có 7 tiêu chí weight khác nhau (0.22 … 0.09) nhưng cùng <c>maxScore = 5</c>, nên bài
    /// được 5/5 mọi tiêu chí phải ra đúng 100%, và bài 3/5 ra 60% — con số này bằng nhau ở cả hai công
    /// thức khi điểm đồng đều, nên phép phân biệt thật nằm ở bài lệch điểm dưới đây.
    /// </summary>
    [Fact]
    public async Task Run_ReportUsesEqualWeightAverage_NotWeighted()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);

        // Bài "Excellent" đạt 5/5 ở MỌI tiêu chí; "Good" đạt 3/5; "Weak" 0/5.
        var run = await Service(t, AiMock(weak: 0m, good: 3m, top: 5m).Object)
            .RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());

        var excellent = run.Samples.Single(s => s.Band == "Excellent");
        var good = run.Samples.Single(s => s.Band == "Good");
        var weak = run.Samples.Single(s => s.Band == "Weak");

        Assert.Equal(100m, excellent.ActualPct);
        Assert.Equal(60m, good.ActualPct);
        Assert.Equal(0m, weak.ActualPct);
        Assert.Equal(7, excellent.Scores.Count);
        // Mức kỳ vọng đi kèm để so "kỳ vọng vs thật" — số đo duy nhất phơi bày self-scoring bias.
        Assert.All(excellent.Scores, s => Assert.Equal(5, s.ExpectedLevel));
        Assert.All(weak.Scores, s => Assert.Equal(0, s.ExpectedLevel));
    }

    /// <summary>Bài mẫu là văn bản ⇒ không có số đo cách nói (F11). Cờ cấu trúc, không giấu được.</summary>
    [Fact]
    public async Task Run_AlwaysReportsDeliveryMetricsUnavailable()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        var run = await Service(t).RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());
        Assert.False(run.DeliveryMetricsAvailable);
        Assert.Equal(7, run.PromptVersion);
    }

    // ── (6) Lịch sử — nguồn để so trước/sau ─────────────────────────────────────────────────

    [Fact]
    public async Task History_ReturnsRunsNewestFirst_WithFingerprint()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t);
        var svc = Service(t);
        await svc.RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());
        await svc.RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest(Question: "câu khác"));

        var history = await svc.HistoryAsync(JobCategory.BE, "vi");

        Assert.Equal(2, history.Count);
        Assert.True(history[0].CreatedAt >= history[1].CreatedAt);
        // Cùng thước đo ⇒ cùng vân tay ⇒ điểm khác nhau là NHIỄU MODEL, không phải đổi thước.
        Assert.Equal(history[0].RubricFingerprint, history[1].RubricFingerprint);
        Assert.All(history, r => Assert.Equal(64, r.RubricFingerprint.Length));
    }

    /// <summary>Lịch sử của (nghề, ngôn ngữ) này KHÔNG lẫn lượt của tổ hợp khác.</summary>
    [Fact]
    public async Task History_ScopedToCategoryAndLanguage()
    {
        using var t = new TestDb();
        await SeedRubricWithLevelsAsync(t, JobCategory.BE, "vi");
        await new AdminB2CRubricService(t.Db).GetAsync(JobCategory.FE, "vi");
        var svc = Service(t);
        await svc.RunAsync(Guid.NewGuid(), JobCategory.BE, "vi", new AdminRubricPreviewRequest());

        Assert.Single(await svc.HistoryAsync(JobCategory.BE, "vi"));
        Assert.Empty(await svc.HistoryAsync(JobCategory.FE, "vi"));
        Assert.Empty(await svc.HistoryAsync(JobCategory.BE, "en"));
    }

    // ── (7) AI gợi ý mốc — không ghi DB ─────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestLevels_ReturnsProposal_WithoutTouchingDb()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();

        var suggester = new Mock<IAiServiceLevelSuggester>();
        suggester.Setup(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<LevelSuggestionInput>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string? _, string? _,
                IReadOnlyList<LevelSuggestionInput> criteria, CancellationToken _) =>
                criteria.Select(c => new SuggestedLevelSet(c.CriterionId,
                    [new SuggestedLevel(0, "trống"), new SuggestedLevel(5, "đủ ý")])).ToList());

        var result = await Service(t, suggester: suggester.Object)
            .SuggestLevelsAsync(JobCategory.BE, "vi", null);

        Assert.Equal(7, result.Criteria.Count);
        Assert.All(result.Criteria, c => Assert.Equal(2, c.Levels.Count));
        // KHÔNG ghi gì xuống DB — lưu phải đi qua đúng một cửa PUT (giữ luật bump ở một chỗ).
        Assert.Empty(await t.Db.RubricLevels.ToListAsync());
    }

    /// <summary>AI lỗi ⇒ ném ra ngoài (controller → 502), CỐ Ý không fallback dải mặc định.</summary>
    [Fact]
    public async Task SuggestLevels_AiFails_Throws_NoFallback()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();

        var suggester = new Mock<IAiServiceLevelSuggester>();
        suggester.Setup(s => s.SuggestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<LevelSuggestionInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamServiceException("AIService sập"));

        await Assert.ThrowsAsync<DownstreamServiceException>(
            () => Service(t, suggester: suggester.Object).SuggestLevelsAsync(JobCategory.BE, "vi", null));
        Assert.Empty(await t.Db.RubricLevels.ToListAsync());
    }
}
