using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// GHIM bộ tiêu chí ở tầng buổi luyện (B2C) — đối xứng bản B2B đã có ở
/// <see cref="RubricVersionPinTests"/>, nhưng có thêm một trục mà B2B không có: <b>CHỦ</b> bộ tiêu chí.
///
/// <para>Admin sửa bộ chuẩn hệ thống, và chính ứng viên cũng sửa được rubric riêng của mình (BC16) —
/// cả hai đều có thể xảy ra giữa buổi. Mọi hỏng hóc ở khu vực này đều IM LẶNG (mất điểm / màn kết quả
/// trống, không exception nào nổ), nên các test dưới đây khoá đúng những đường không có triệu chứng.</para>
/// </summary>
public class B2CRubricPinTests
{
    private const string Vi = "vi";

    /// <summary>Bộ chuẩn hệ thống (candidate_id NULL) có v1 ĐÃ HẠ CỜ và v2 đang active.</summary>
    private static (List<RubricCriterion> V1, List<RubricCriterion> V2) SeedSystemTwoVersions(
        TestDb t, JobCategory cat = JobCategory.BE)
    {
        var v1 = new List<RubricCriterion>
        {
            TestDb.Criterion(cat, version: 1, active: false, name: "Giao tiếp"),
            TestDb.Criterion(cat, version: 1, active: false, name: "Chiều sâu")
        };
        var v2 = new List<RubricCriterion>
        {
            TestDb.Criterion(cat, version: 2, active: true, name: "Giao tiếp"),
            TestDb.Criterion(cat, version: 2, active: true, name: "Chiều sâu")
        };
        t.Db.RubricCriteria.AddRange(v1);
        t.Db.RubricCriteria.AddRange(v2);
        t.Db.SaveChanges();
        return (v1, v2);
    }

    // ── (1) Loader — trái tim của cả thay đổi ────────────────────────────────────────────────

    /// <summary>
    /// Buổi ghim v1 của BỘ CHUẨN trong khi admin đã lưu v2 (v1 bị hạ cờ) ⇒ vẫn nạp ĐỦ bộ v1.
    ///
    /// Test quan trọng nhất file. Nếu vế <c>is_active</c> còn nằm trước nhánh ghim thì buổi này nạp về
    /// 0 tiêu chí ⇒ AnswerService bỏ qua publish ⇒ answer KHÔNG BAO GIỜ được chấm ⇒ session không đóng
    /// ⇒ ứng viên mất 1 credit mà không có kết quả (PAY-13). Không lỗi nào nổ ra.
    /// </summary>
    [Fact]
    public async Task Loader_PinnedToDeactivatedSystemVersion_StillLoadsThatWholeVersion()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (v1, _) = SeedSystemTwoVersions(t);

        var loaded = await RubricCriteriaLoader.LoadAsync(t.Db,
            new RubricScopeKey(null, candidate, JobCategory.BE, Vi, B2CRubricVersion: 1));

        Assert.Equal(2, loaded.Count);                        // ĐỦ bộ, không phải 0
        Assert.All(loaded, c => Assert.Equal(1, c.Version));
        Assert.All(loaded, c => Assert.False(c.IsActive));    // đã hạ cờ mà vẫn dùng để chấm
        Assert.Equal(v1.Select(c => c.Id).OrderBy(x => x), loaded.Select(c => c.Id).OrderBy(x => x));
    }

    /// <summary>Chiều ngược lại: buổi ghim v2 không được nhặt nhầm tiêu chí v1.</summary>
    [Fact]
    public async Task Loader_PinnedToCurrentSystemVersion_DoesNotLeakOlderVersion()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (_, v2) = SeedSystemTwoVersions(t);

        var loaded = await RubricCriteriaLoader.LoadAsync(t.Db,
            new RubricScopeKey(null, candidate, JobCategory.BE, Vi, B2CRubricVersion: 2));

        Assert.Equal(v2.Select(c => c.Id).OrderBy(x => x), loaded.Select(c => c.Id).OrderBy(x => x));
    }

    /// <summary>
    /// Buổi ghim BỘ CHUẨN (owner = null) trong khi ứng viên ĐÃ có rubric riêng đang active ⇒ vẫn phải
    /// nạp bộ chuẩn.
    ///
    /// Đây là vế mà ghim-mỗi-version không cứu được: <c>ResolveOwnerAsync</c> hỏi trạng thái ở THỜI
    /// ĐIỂM GỌI, nên ứng viên bấm "Lưu rubric riêng" giữa buổi làm callback resolve ra chủ mới ⇒ mọi
    /// criterionId vừa gửi đi chấm bị guard E8 coi là lạ và BỎ ⇒ answer mất sạch điểm, im lặng.
    /// </summary>
    [Fact]
    public async Task Loader_PinnedToSystemOwner_IgnoresCustomRubricSavedMidSession()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var system = TestDb.Criterion(JobCategory.BE, version: 1, active: true, name: "Giao tiếp");
        // Ứng viên lưu rubric riêng GIỮA buổi — cùng nghề, cùng ngôn ngữ, cũng active.
        var custom = TestDb.Criterion(JobCategory.BE, version: 1, active: true,
            name: "Tự đặt", candidateId: candidate);
        t.Db.RubricCriteria.AddRange(system, custom);
        await t.Db.SaveChangesAsync();

        var loaded = await RubricCriteriaLoader.LoadAsync(t.Db,
            new RubricScopeKey(null, candidate, JobCategory.BE, Vi,
                B2COwnerId: null, B2CRubricVersion: 1));

        Assert.Single(loaded);
        Assert.Equal(system.Id, loaded[0].Id);
    }

    /// <summary>
    /// Buổi ghim RUBRIC RIÊNG v1 trong khi ứng viên đã lưu v2 ⇒ nạp đúng v1 của chính họ, và không
    /// lẫn bộ chuẩn.
    ///
    /// "v2 của rubric riêng" và "v2 của bộ chuẩn" là hai bộ khác nhau mang cùng một con số — đó là lý
    /// do con dấu phải mang CẢ chủ lẫn phiên bản.
    /// </summary>
    [Fact]
    public async Task Loader_PinnedToCustomOwnerVersion_LoadsThatOwnersVersionOnly()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var systemV1 = TestDb.Criterion(JobCategory.BE, version: 1, active: true, name: "Giao tiếp");
        var customV1 = TestDb.Criterion(JobCategory.BE, version: 1, active: false,
            name: "Tự đặt", candidateId: candidate);
        var customV2 = TestDb.Criterion(JobCategory.BE, version: 2, active: true,
            name: "Tự đặt", candidateId: candidate);
        t.Db.RubricCriteria.AddRange(systemV1, customV1, customV2);
        await t.Db.SaveChangesAsync();

        var loaded = await RubricCriteriaLoader.LoadAsync(t.Db,
            new RubricScopeKey(null, candidate, JobCategory.BE, Vi,
                B2COwnerId: candidate, B2CRubricVersion: 1));

        Assert.Single(loaded);
        Assert.Equal(customV1.Id, loaded[0].Id);
    }

    /// <summary>
    /// Buổi có TRƯỚC cặp cột ghim (cả hai null) ⇒ hành vi Y HỆT hôm nay: bộ đang hiệu lực, ưu tiên
    /// rubric riêng.
    /// </summary>
    [Fact]
    public async Task Loader_NoPin_FallsBackToActiveSet_PreferringCustomRubric()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var system = TestDb.Criterion(JobCategory.BE, version: 1, active: true, name: "Giao tiếp");
        var custom = TestDb.Criterion(JobCategory.BE, version: 1, active: true,
            name: "Tự đặt", candidateId: candidate);
        var stale = TestDb.Criterion(JobCategory.BE, version: 0, active: false,
            name: "Cũ", candidateId: candidate);
        t.Db.RubricCriteria.AddRange(system, custom, stale);
        await t.Db.SaveChangesAsync();

        var loaded = await RubricCriteriaLoader.LoadAsync(t.Db,
            new RubricScopeKey(null, candidate, JobCategory.BE, Vi));

        Assert.Single(loaded);
        Assert.Equal(custom.Id, loaded[0].Id);   // rubric riêng thắng; bản hạ cờ bị loại
    }

    /// <summary><see cref="RubricCriteriaLoader.KeyFor"/> phải chuyển CẢ HAI cột con dấu vào khoá.</summary>
    [Fact]
    public void KeyFor_B2CSession_CarriesOwnerAndVersion()
    {
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress, JobCategory.FE);
        session.B2CRubricOwnerId = candidate;
        session.B2CRubricVersion = 7;

        var key = RubricCriteriaLoader.KeyFor(session);

        Assert.Equal(candidate, key.B2COwnerId);
        Assert.Equal(7, key.B2CRubricVersion);
        Assert.Null(key.CampaignId);
    }

    // ── (1a) Đường GÁN con dấu lúc tạo buổi ─────────────────────────────────────────────────
    //
    // Mutation "B2CRubricOwnerId = null luôn" chạy qua toàn bộ test XANH ở lượt đầu: các test loader
    // bên trên gán con dấu BẰNG TAY lên entity nên chúng phủ chỗ ĐỌC mà không phủ chỗ GHI. Đó là khe
    // nguy hiểm — hỏng ở đây thì mọi buổi của người có rubric riêng bị ghim "bộ chuẩn", tức họ luyện
    // bằng thước mình tự đặt nhưng bị chấm bằng thước hệ thống, im lặng.

    private static PracticeService BuildPractice(TestDb t)
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        // Đặt CẢ HAI overload: đường B2C không-adaptive gọi bản 4 tham số, còn bản 6 tham số là đường
        // có focusCriteria/count. Chỉ đặt một bản thì bản kia trả `null` và service ném "AIService
        // không trả về câu hỏi nào" — một lỗi của TEST, không phải của con dấu đang đo.
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GeneratedQuestion { Content = "Q1" }]);
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GeneratedQuestion { Content = "Q1" }]);

        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);
    }

    /// <summary>Ứng viên CÓ rubric riêng ⇒ buổi mới ghim đúng chủ + phiên bản của bộ riêng đó.</summary>
    [Fact]
    public async Task CreateSession_WithCustomRubric_StampsOwnerAndVersion()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.RubricCriteria.AddRange(
            TestDb.Criterion(JobCategory.BE, version: 1, active: false, name: "Tự đặt", candidateId: candidate),
            TestDb.Criterion(JobCategory.BE, version: 4, active: true, name: "Tự đặt", candidateId: candidate),
            TestDb.Criterion(JobCategory.BE, version: 1, active: true, name: "Giao tiếp"));
        await t.Db.SaveChangesAsync();

        var res = await BuildPractice(t).CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var session = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == res.Id);
        Assert.Equal(candidate, session.B2CRubricOwnerId);
        Assert.Equal(4, session.B2CRubricVersion);
    }

    /// <summary>Không có rubric riêng ⇒ ghim BỘ CHUẨN (chủ null) kèm đúng phiên bản đang hiệu lực.</summary>
    [Fact]
    public async Task CreateSession_WithoutCustomRubric_StampsSystemOwner()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, version: 6, active: true, name: "Giao tiếp"));
        await t.Db.SaveChangesAsync();

        var res = await BuildPractice(t).CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var session = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == res.Id);
        Assert.Null(session.B2CRubricOwnerId);
        Assert.Equal(6, session.B2CRubricVersion);
    }

    // ── (1b) Khoá chống hai admin cùng lưu một (nghề, ngôn ngữ) ─────────────────────────────

    /// <summary>
    /// Hai bộ chuẩn cùng (nghề, ngôn ngữ, phiên bản, tên) ⇒ DB từ chối.
    ///
    /// Không có ràng buộc này thì hai admin cùng bấm Lưu sẽ cùng đọc <c>max(version)</c> ra một số rồi
    /// cùng ghi ⇒ 14 dòng active cùng lúc ⇒ loader nạp 14 tiêu chí và <c>criteria[0].Version</c> phụ
    /// thuộc may rủi. Đọc <c>max(version)</c> KHÔNG phải trọng tài.
    ///
    /// <para>⚠ SQLite (EF Core 10) DỰNG được index có filter qua <c>EnsureCreated</c> và enforce thật —
    /// đã kiểm bằng chính test này, và mutation gỡ index làm nó chuyển ĐỎ. Nhưng đó là may mắn về ngữ
    /// nghĩa trùng nhau giữa hai engine, không phải bảo đảm: L3 Postgres vẫn là nơi duy nhất chứng minh
    /// câu filter chạy đúng trên bản thật.</para>
    /// </summary>
    [Fact]
    public async Task SystemRubric_DuplicateVersionAndName_Rejected()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, version: 3, name: "Giao tiếp"));
        await t.Db.SaveChangesAsync();

        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, version: 3, name: "Giao tiếp"));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    /// <summary>
    /// Vế ÂM của cùng ràng buộc: hai ứng viên KHÁC NHAU hoàn toàn được phép có rubric riêng trùng
    /// (nghề, ngôn ngữ, phiên bản, tên) — version của rubric riêng đánh số độc lập theo từng người.
    /// Thiếu filter <c>candidate_id IS NULL</c> thì index chặn oan toàn bộ đường BC16.
    /// </summary>
    [Fact]
    public async Task CustomRubric_SameKeyForDifferentCandidates_Allowed()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(
            TestDb.Criterion(JobCategory.BE, version: 1, name: "Giao tiếp", candidateId: Guid.NewGuid()),
            TestDb.Criterion(JobCategory.BE, version: 1, name: "Giao tiếp", candidateId: Guid.NewGuid()));

        await t.Db.SaveChangesAsync();   // không được ném

        Assert.Equal(2, await t.Db.RubricCriteria.CountAsync(c => c.CandidateId != null));
    }

    // ── (1c) Republisher — đường cứu answer kẹt phải dùng CÙNG thước đo ─────────────────────

    /// <summary>
    /// Answer B2C được cứu bằng republisher phải chấm bằng đúng bộ đã GHIM, không phải bộ đang hiệu lực.
    ///
    /// Lệch ở đây là cùng một answer sinh HAI <c>rubric_version</c> ⇒ <c>attemptsForVersion</c> không
    /// bao giờ đủ N ⇒ answer kẹt <c>Scoring</c> VĨNH VIỄN. Mutation "bỏ con dấu B2C khỏi khoá phạm vi"
    /// chạy qua toàn bộ test XANH ở lượt đầu — đường republisher của B2C chưa từng được phủ, trong khi
    /// bản B2B thì có. Đúng chỗ F11 và đáp án mẫu đã dính.
    /// </summary>
    [Fact]
    public async Task Republisher_B2C_UsesPinnedSetNotLatestActive()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        // Ứng viên đã lưu rubric riêng SAU khi buổi bắt đầu; buổi ghim bộ CHUẨN v1.
        var systemV1 = TestDb.Criterion(JobCategory.BE, version: 1, active: true, name: "Giao tiếp");
        var custom = TestDb.Criterion(JobCategory.BE, version: 1, active: true,
            name: "Tự đặt", candidateId: candidate);
        var session = TestDb.Session(candidate, SessionStatus.InProgress, JobCategory.BE);
        session.B2CRubricOwnerId = null;
        session.B2CRubricVersion = 1;
        t.Db.AddRange(systemV1, custom, session);
        var question = TestDb.Question(session.Id);
        t.Db.Add(question);
        t.Db.PracticeAnswers.Add(TestDb.Answer(
            session.Id, question.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-30), lastPublished: null));
        await t.Db.SaveChangesAsync();

        var job = await RepublishAndCaptureAsync(t);

        Assert.Single(job.Criteria);
        Assert.Equal(systemV1.Id, job.Criteria[0].CriterionId);
        Assert.DoesNotContain(job.Criteria, c => c.CriterionId == custom.Id);
    }

    private static async Task<ScoringJob> RepublishAndCaptureAsync(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o =>
            o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var pub = new Mock<IScoringJobPublisher>();
        ScoringJob? captured = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);

        var republisher = new StuckAnswerRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(), pub.Object,
            Options.Create(new RepublisherSettings { BatchSize = 200 }),
            Options.Create(new ScoringOptions()),
            NullLogger<StuckAnswerRepublisher>.Instance);

        var mi = typeof(StuckAnswerRepublisher).GetMethod(
            "ScanOnceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)mi.Invoke(republisher, [CancellationToken.None])!;

        Assert.NotNull(captured);
        return captured!;
    }

    // ── (2) Tổng kết buổi (BC9) — bản sao THỨ TƯ, phải đi qua loader ─────────────────────────

    /// <summary>
    /// Buổi ghim v1, admin đã lên v2 ⇒ breakdown vẫn ĐỦ dòng và <c>overall &gt; 0</c>.
    ///
    /// Trước khi gom vào loader, <c>SessionResultService</c> tự query và lọc <c>IsActive</c> ⇒ nạp bộ
    /// v2 (cùng TÊN nhưng id MỚI) trong khi điểm đã chấm đánh khoá theo id v1 ⇒ <c>TryGetValue</c>
    /// trượt toàn bộ ⇒ 0 dòng breakdown, <c>overall = 0</c>, kèm đúng một dòng LogWarning. Người dùng
    /// trả 1 credit, chấm xong, mở ra thấy màn kết quả trống.
    /// </summary>
    [Fact]
    public async Task SessionResult_PinnedToDeactivatedVersion_KeepsBreakdownAndScore()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (v1, _) = SeedSystemTwoVersions(t);

        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.B2CRubricVersion = 1;          // ghim bộ chuẩn v1
        session.B2CRubricOwnerId = null;
        var q = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, answer);
        // Điểm được chấm trên chính các tiêu chí v1 (4/5 = 80%).
        t.Db.AnswerScores.AddRange(v1.Select(c => new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = c.Id,
            AttemptNo = 1,
            Score = 4m,
            Reasoning = "x",
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        }));
        await t.Db.SaveChangesAsync();

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var rows = await t.Db.SessionCriterionScores.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.Equal(2, rows.Count);                                     // KHÔNG phải 0
        Assert.Equal(v1.Select(c => c.Id).OrderBy(x => x), rows.Select(r => r.CriterionId).OrderBy(x => x));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(80m, s.OverallScore);                                // KHÔNG phải 0
    }

    /// <summary>
    /// Buổi ghim rubric RIÊNG ⇒ breakdown lấy đúng bộ của ứng viên, không lẫn bộ chuẩn cùng nghề.
    /// </summary>
    [Fact]
    public async Task SessionResult_PinnedToCustomOwner_UsesThatOwnersCriteria()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var system = TestDb.Criterion(JobCategory.BE, version: 1, active: true, name: "Giao tiếp");
        var custom = TestDb.Criterion(JobCategory.BE, version: 1, active: true,
            name: "Tự đặt", candidateId: candidate);
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.B2CRubricOwnerId = candidate;
        session.B2CRubricVersion = 1;
        var q = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(system, custom, session, q, answer);
        t.Db.AnswerScores.Add(new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = custom.Id,
            AttemptNo = 1,
            Score = 5m,
            Reasoning = "x",
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var rows = await t.Db.SessionCriterionScores.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(custom.Id, rows[0].CriterionId);
    }
}
