using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// GHIM phiên bản rubric ở tầng buổi thi (B2B).
///
/// HR được sửa mốc điểm khi campaign đang Active; thay đổi chỉ áp cho ứng viên thi SAU. Buổi đang
/// chạy dở phải tiếp tục được chấm bằng đúng thước đo lúc nó bắt đầu — kể cả khi bộ tiêu chí đó đã
/// bị hạ cờ <c>is_active</c>. Mọi hỏng hóc ở khu vực này đều IM LẶNG (điểm sai, không exception),
/// nên phần lớn test dưới đây khoá đúng những đường không có triệu chứng.
/// </summary>
public class RubricVersionPinTests
{
    // ── Seed helper ─────────────────────────────────────────────────────────────────
    // Một campaign có bộ v1 ĐÃ BỊ HẠ CỜ (HR đã sửa mốc) và bộ v2 đang active.
    private static (Guid CampaignId, List<RubricCriterion> V1, List<RubricCriterion> V2)
        SeedTwoVersions(TestDb t, JobCategory cat = JobCategory.BE)
    {
        var campaignId = Guid.NewGuid();
        var v1 = new List<RubricCriterion>
        {
            TestDb.Criterion(cat, version: 1, active: false, campaignId: campaignId, name: "Communication"),
            TestDb.Criterion(cat, version: 1, active: false, campaignId: campaignId, name: "Technical depth")
        };
        var v2 = new List<RubricCriterion>
        {
            TestDb.Criterion(cat, version: 2, active: true, campaignId: campaignId, name: "Communication"),
            TestDb.Criterion(cat, version: 2, active: true, campaignId: campaignId, name: "Technical depth")
        };
        t.Db.RubricCriteria.AddRange(v1);
        t.Db.RubricCriteria.AddRange(v2);
        t.Db.SaveChanges();
        return (campaignId, v1, v2);
    }

    // ── (1) Loader — trái tim của cả thay đổi ───────────────────────────────────────

    /// <summary>
    /// Buổi ghim v1 trong khi campaign đã sang v2 (v1 bị hạ cờ) ⇒ vẫn phải nạp ĐỦ bộ v1.
    ///
    /// Đây là test quan trọng nhất file. Nếu vế <c>is_active</c> còn nằm ở nhánh B2B thì buổi này nạp
    /// về 0 tiêu chí ⇒ AnswerService bỏ qua publish ⇒ answer KHÔNG BAO GIỜ được chấm ⇒ session không
    /// đóng ⇒ ứng viên mất 1 credit mà không có kết quả (PAY-13). Không lỗi nào nổ ra.
    /// </summary>
    [Fact]
    public async Task Loader_PinnedToDeactivatedVersion_StillLoadsThatWholeVersion()
    {
        using var t = new TestDb();
        var (campaignId, v1, _) = SeedTwoVersions(t);

        var loaded = await RubricCriteriaLoader.LoadAsync(
            t.Db, new RubricScopeKey(campaignId, null, null, CampaignRubricVersion: 1));

        Assert.Equal(2, loaded.Count);                       // ĐỦ bộ, không phải 0
        Assert.All(loaded, c => Assert.Equal(1, c.Version));
        Assert.All(loaded, c => Assert.False(c.IsActive));   // đúng: đã hạ cờ mà vẫn dùng để chấm
        Assert.Equal(v1.Select(c => c.Id).OrderBy(x => x), loaded.Select(c => c.Id).OrderBy(x => x));
    }

    // Buổi ghim v2 KHÔNG được nhặt nhầm tiêu chí v1 (chiều ngược lại của test trên).
    [Fact]
    public async Task Loader_PinnedToCurrentVersion_DoesNotLeakOlderVersion()
    {
        using var t = new TestDb();
        var (campaignId, _, v2) = SeedTwoVersions(t);

        var loaded = await RubricCriteriaLoader.LoadAsync(
            t.Db, new RubricScopeKey(campaignId, null, null, CampaignRubricVersion: 2));

        Assert.Equal(2, loaded.Count);
        Assert.Equal(v2.Select(c => c.Id).OrderBy(x => x), loaded.Select(c => c.Id).OrderBy(x => x));
    }

    // Buổi có TRƯỚC cột ghim (pin null) → rơi về luật cũ `is_active`. Sau backfill không nên tới
    // được nhánh này, nhưng nó phải cư xử y hệt hành vi trước thay đổi chứ không được trả cả hai bộ.
    [Fact]
    public async Task Loader_NoPin_FallsBackToIsActive_LikeBeforeThisChange()
    {
        using var t = new TestDb();
        var (campaignId, _, v2) = SeedTwoVersions(t);

        var loaded = await RubricCriteriaLoader.LoadAsync(
            t.Db, new RubricScopeKey(campaignId, null, null));

        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, c => Assert.True(c.IsActive));
        Assert.Equal(v2.Select(c => c.Id).OrderBy(x => x), loaded.Select(c => c.Id).OrderBy(x => x));
    }

    /// <summary>
    /// Thứ tự trả về phải ỔN ĐỊNH theo tên: AnswerService và republisher đều đọc
    /// <c>criteria[0].Version</c> làm con dấu rubric_version cho CẢ lượt chấm.
    ///
    /// ⚠ Id được GÁN NGƯỢC chiều với tên, có chủ đích. `.Include(Levels)` khiến EF tự thêm
    /// <c>ORDER BY r.id</c> để gom collection, nên nếu seed bằng <c>Guid.NewGuid()</c> thì thứ tự trả
    /// về là ngẫu nhiên theo Guid và test này ĐỖ HAY TRƯỢT TÙY MAY — đo bằng mutation: gỡ
    /// <c>OrderBy(Name)</c> mà test vẫn xanh. Ép id ngược chiều tên thì thiếu OrderBy là sai chắc chắn.
    /// </summary>
    [Fact]
    public async Task Loader_ReturnsCriteriaOrderedByName()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        // id tăng dần theo thứ tự NGƯỢC bảng chữ cái ⇒ thứ tự-theo-id khác hẳn thứ tự-theo-tên.
        var seeded = new[] { "Zulu", "Mike", "Alpha" };
        for (var i = 0; i < seeded.Length; i++)
        {
            var c = TestDb.Criterion(JobCategory.BE, version: 3, campaignId: campaignId, name: seeded[i]);
            c.Id = new Guid($"00000000-0000-0000-0000-00000000000{i + 1}");
            t.Db.RubricCriteria.Add(c);
        }
        await t.Db.SaveChangesAsync();

        var loaded = await RubricCriteriaLoader.LoadAsync(
            t.Db, new RubricScopeKey(campaignId, null, null, CampaignRubricVersion: 3));

        Assert.Equal(new[] { "Alpha", "Mike", "Zulu" }, loaded.Select(c => c.Name).ToArray());
    }

    // KeyFor phải LẤY pin từ session — quên vế này là loader luôn chạy nhánh dự phòng `is_active`,
    // tức toàn bộ tính năng không có tác dụng dù mọi test loader ở trên vẫn xanh.
    [Fact]
    public void KeyFor_B2BSession_CarriesPinnedVersion()
    {
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress, campaignId: Guid.NewGuid());
        session.CampaignRubricVersion = 7;

        Assert.Equal(7, RubricCriteriaLoader.KeyFor(session).CampaignRubricVersion);
    }

    // B2C không có khái niệm phiên bản campaign ⇒ pin phải là null, nếu không rubric riêng BC16 bị
    // lọc theo một con số vô nghĩa và nạp về rỗng.
    [Fact]
    public void KeyFor_B2CSession_HasNoPin()
    {
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        session.CampaignRubricVersion = 7;   // rác (không nên có), vẫn không được rò sang khoá B2C

        var key = RubricCriteriaLoader.KeyFor(session);
        Assert.Null(key.CampaignRubricVersion);
        Assert.Null(key.CampaignId);
    }

    // ── (2) Materialize theo phiên bản ──────────────────────────────────────────────

    private static PracticeService Build(TestDb t)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier.Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object, notifier.Object, credits.Object,
            NullLogger<PracticeService>.Instance,
            capacityOptions: Options.Create(new CapacityOptions()));
    }

    private static CreateCampaignSessionRequest Request(
        Guid campaignId, int? rubricVersion = null,
        IReadOnlyList<CampaignCriterionLevelInput>? levels = null)
        => new(campaignId, Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5, levels) },
            RubricVersion: rubricVersion);

    // Campaign bản CŨ chưa gửi rubricVersion ⇒ v1 ⇒ khớp mọi row đang có trên prod (materialize cũ
    // hardcode Version = 1). Đây là vế "không đổi hành vi cho campaign hiện hữu".
    [Fact]
    public async Task Materialize_WithoutRubricVersion_PinsAndCreatesVersion1()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();

        var res = await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(), Request(campaignId));

        var session = await t.NewContext().PracticeSessions.SingleAsync(s => s.Id == res.Id);
        Assert.Equal(1, session.CampaignRubricVersion);
        Assert.All(await t.NewContext().RubricCriteria.Where(c => c.CampaignId == campaignId).ToListAsync(),
            c => Assert.Equal(1, c.Version));
    }

    /// <summary>
    /// Sau khi HR bump: bộ mới mang ĐÚNG số Campaign cấp, bộ cũ bị hạ cờ (KHÔNG xoá — answer_scores
    /// có FK Restrict và điểm đã chấm phải giữ được lai lịch thước đo).
    /// </summary>
    [Fact]
    public async Task Materialize_NewVersion_AddsPinnedSet_AndDeactivatesPrevious()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(), Request(campaignId));

        var res = await Build(t).CreateCampaignSessionAsync(
            Guid.NewGuid(), Request(campaignId, rubricVersion: 2));

        var db = t.NewContext();
        var all = await db.RubricCriteria.Where(c => c.CampaignId == campaignId).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all.Where(c => c.Version == 1), c => Assert.False(c.IsActive));
        Assert.All(all.Where(c => c.Version == 2), c => Assert.True(c.IsActive));
        Assert.Equal(2, (await db.PracticeSessions.SingleAsync(s => s.Id == res.Id)).CampaignRubricVersion);
    }

    /// <summary>
    /// Interview chỉ CHÉP số Campaign cấp, tuyệt đối không tự tính <c>max(Version)+1</c>.
    ///
    /// Materialize là LAZY: Campaign có thể đã ở v5 trong khi Interview mới có v1 (nhiều lần sửa mà
    /// không ai Start ở giữa). Tự đánh số sẽ ra v2 ⇒ số HR nhìn thấy và số nằm trên answer_scores
    /// lệch nhau vĩnh viễn — hai nhãn cho cùng một thứ, đúng lớp lỗi BK23 sinh ra để chặn.
    /// </summary>
    [Fact]
    public async Task Materialize_NeverComputesNextVersionItself_CopiesWhatCampaignSent()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(), Request(campaignId));   // v1 tồn tại

        // Campaign nhảy thẳng lên v5 (bump nhiều lần mà không ai Start ở giữa) — LỖ SỐ là bình thường,
        // đây là ĐỊNH DANH chứ không phải bộ đếm.
        var res = await Build(t).CreateCampaignSessionAsync(
            Guid.NewGuid(), Request(campaignId, rubricVersion: 5));

        var db = t.NewContext();
        var versions = await db.RubricCriteria
            .Where(c => c.CampaignId == campaignId).Select(c => c.Version).Distinct().ToListAsync();
        Assert.Equal(new[] { 1, 5 }, versions.OrderBy(v => v).ToArray());   // KHÔNG có 2
        Assert.Equal(5, (await db.PracticeSessions.SingleAsync(s => s.Id == res.Id)).CampaignRubricVersion);
    }

    // Buổi thứ hai của CÙNG phiên bản: không materialize lại, nhưng vẫn phải được ghim — thiếu vế này
    // thì mọi ứng viên từ người thứ hai trở đi rơi về nhánh dự phòng `is_active`.
    [Fact]
    public async Task Materialize_SameVersionTwice_DoesNotDuplicate_ButStillPinsSession()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(), Request(campaignId, rubricVersion: 3));

        var second = await Build(t).CreateCampaignSessionAsync(
            Guid.NewGuid(), Request(campaignId, rubricVersion: 3));

        var db = t.NewContext();
        Assert.Equal(1, await db.RubricCriteria.CountAsync(c => c.CampaignId == campaignId));
        Assert.Equal(3, (await db.PracticeSessions.SingleAsync(s => s.Id == second.Id)).CampaignRubricVersion);
    }

    /// <summary>
    /// Hai ứng viên bấm Start cùng lúc ngay sau khi HR bump ⇒ cả hai đều thấy "chưa có bộ v2" ⇒ cả hai
    /// cùng chèn. Không có ràng buộc DB thì campaign có HAI bộ v2, mẫu số điểm tổng (INT-10) sai gấp
    /// đôi mà không lỗi nào nổ. UNIQUE (campaign_id, version, name) là thứ duy nhất chặn được.
    /// </summary>
    [Fact]
    public async Task ConcurrentMaterialize_SameVersion_ViolatesUniqueIndex_NoDuplicateRows()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();

        RubricCriterion Row() => TestDb.Criterion(
            JobCategory.BE, version: 2, campaignId: campaignId, name: "Communication");

        t.Db.RubricCriteria.Add(Row());
        await t.Db.SaveChangesAsync();

        // Context thứ hai = "ứng viên kia" đã đọc trước khi ta commit → cùng chèn bộ v2.
        var rival = t.NewContext();
        rival.RubricCriteria.Add(Row());
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => rival.SaveChangesAsync());

        Assert.Equal(1, await t.NewContext().RubricCriteria
            .CountAsync(c => c.CampaignId == campaignId && c.Version == 2));
    }

    // Rubric B2C có campaign_id NULL và trùng `name` khắp nơi (mỗi candidate một bộ "Communication").
    // Unique index PHẢI có filter, nếu không nó chặn oan toàn bộ đường rubric riêng BC16.
    [Fact]
    public async Task UniqueIndex_IsFiltered_DoesNotBlockB2CRubricsSharingNames()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, candidateId: Guid.NewGuid(), name: "Communication"));
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, candidateId: Guid.NewGuid(), name: "Communication"));
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, name: "Communication"));   // seed dùng chung

        await t.Db.SaveChangesAsync();   // không được ném

        Assert.Equal(3, await t.NewContext().RubricCriteria.CountAsync(c => c.Name == "Communication"));
    }

    // ── (3) Mốc điểm (E9 hard-anchor) ───────────────────────────────────────────────

    [Fact]
    public async Task Materialize_WithLevels_PersistsOneRowPerLevel()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var levels = new[]
        {
            new CampaignCriterionLevelInput(0, "CÓ: không nêu được ý nào | CÒN THIẾU: toàn bộ"),
            new CampaignCriterionLevelInput(3, "CÓ: nêu đúng khái niệm | CÒN THIẾU: ví dụ, đánh đổi"),
            new CampaignCriterionLevelInput(5, "CÓ: khái niệm + ví dụ + đánh đổi | CÒN THIẾU: —")
        };

        await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(), Request(campaignId, levels: levels));

        var saved = await t.NewContext().RubricCriteria
            .Include(c => c.Levels).SingleAsync(c => c.CampaignId == campaignId);
        Assert.Equal(new[] { 0, 3, 5 }, saved.Levels.Select(l => l.Score).OrderBy(s => s).ToArray());
        Assert.All(saved.Levels, l => Assert.False(string.IsNullOrWhiteSpace(l.Descriptor)));
    }

    // Không có mốc là trạng thái HỢP LỆ (AIService rơi về dải mặc định 0..maxScore) — không phải lỗi,
    // và không được đẻ ra level rác.
    [Fact]
    public async Task Materialize_WithoutLevels_CreatesNoLevelRows()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();

        await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(), Request(campaignId));

        var saved = await t.NewContext().RubricCriteria
            .Include(c => c.Levels).SingleAsync(c => c.CampaignId == campaignId);
        Assert.Empty(saved.Levels);
    }

    // ── (4) Guard thang méo ở controller ────────────────────────────────────────────

    private static InternalSessionsController Controller(Mock<IPracticeService> svc)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tok" })
            .Build();
        return new InternalSessionsController(
            svc.Object, config, NullLogger<InternalSessionsController>.Instance);
    }

    private static async Task<IReadOnlyList<CampaignCriterionInput>> CapturedCriteria(
        params CampaignCriterionInput[] criteria)
    {
        var svc = new Mock<IPracticeService>();
        CreateCampaignSessionRequest? captured = null;
        svc.Setup(s => s.GetOrCreateCampaignSessionAsync(
                It.IsAny<Guid>(), It.IsAny<CreateCampaignSessionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateCampaignSessionRequest, CancellationToken>((_, r, _) => captured = r)
            .ReturnsAsync(new PracticeSessionResponse(
                Guid.NewGuid(), "Ready", "BE", "vi", null, null, DateTime.UtcNow, null, []));

        var req = new CreateCampaignSessionInternalRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            new[] { "Q1" }, criteria);

        await Controller(svc).CreateOrGetCampaignSession(req, "tok", CancellationToken.None);
        Assert.NotNull(captured);
        return captured!.Criteria;
    }

    /// <summary>
    /// Thang méo phải bị BỎ MỐC (rơi về dải mặc định = chấm y như trước tính năng này), không được đi
    /// tiếp xuống bộ chấm. Hai mốc trùng <c>score</c> làm phép snap điểm về mức chọn KHÔNG XÁC ĐỊNH ở
    /// cả hai phía Python và C#; mốc vượt <c>maxScore</c> neo điểm vào mức ngoài thang. Cả hai đều ra
    /// điểm "trông hợp lệ" — thà chấm như hôm nay còn hơn chấm bằng thang méo.
    /// </summary>
    [Theory]
    [InlineData(3, 3)]    // trùng điểm
    [InlineData(0, 99)]   // vượt maxScore
    [InlineData(-1, 4)]   // âm
    public async Task Controller_DropsLevels_WhenScaleIsMalformed(int a, int b)
    {
        var criteria = await CapturedCriteria(new CampaignCriterionInput(
            "Communication", null, 1.0m, 5,
            new[] { new CampaignCriterionLevelInput(a, "x"), new CampaignCriterionLevelInput(b, "y") }));

        Assert.Null(Assert.Single(criteria).Levels);
    }

    [Fact]
    public async Task Controller_DropsLevels_WhenDescriptorIsBlank()
    {
        var criteria = await CapturedCriteria(new CampaignCriterionInput(
            "Communication", null, 1.0m, 5,
            new[] { new CampaignCriterionLevelInput(0, "  "), new CampaignCriterionLevelInput(5, "ok") }));

        Assert.Null(Assert.Single(criteria).Levels);
    }

    // Bỏ mốc phải là bỏ THEO TỪNG TIÊU CHÍ — một tiêu chí hỏng không được kéo cả campaign về dải
    // mặc định, vì như vậy một ô HR gõ sai làm bay thước đo của mọi tiêu chí khác.
    [Fact]
    public async Task Controller_MalformedCriterion_DoesNotStripLevelsFromHealthyOnes()
    {
        var criteria = await CapturedCriteria(
            new CampaignCriterionInput("Broken", null, 0.5m, 5,
                new[] { new CampaignCriterionLevelInput(9, "ngoài thang") }),
            new CampaignCriterionInput("Healthy", null, 0.5m, 5,
                new[] { new CampaignCriterionLevelInput(0, "thấp"), new CampaignCriterionLevelInput(5, "cao") }));

        Assert.Null(criteria.Single(c => c.Name == "Broken").Levels);
        Assert.Equal(2, criteria.Single(c => c.Name == "Healthy").Levels!.Count);
    }

    [Fact]
    public async Task Controller_ValidLevels_PassThroughUntouched()
    {
        var criteria = await CapturedCriteria(new CampaignCriterionInput(
            "Communication", null, 1.0m, 5,
            new[] { new CampaignCriterionLevelInput(0, "thấp"), new CampaignCriterionLevelInput(5, "cao") }));

        Assert.Equal(2, Assert.Single(criteria).Levels!.Count);
    }

    // Hợp đồng dây: controller phải CHUYỂN TIẾP rubricVersion xuống service. Quên vế này thì mọi buổi
    // ghim v1 dù Campaign gửi gì — tính năng chết câm, không lỗi nào nổ.
    [Fact]
    public async Task Controller_ForwardsRubricVersion_ToService()
    {
        var svc = new Mock<IPracticeService>();
        CreateCampaignSessionRequest? captured = null;
        svc.Setup(s => s.GetOrCreateCampaignSessionAsync(
                It.IsAny<Guid>(), It.IsAny<CreateCampaignSessionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateCampaignSessionRequest, CancellationToken>((_, r, _) => captured = r)
            .ReturnsAsync(new PracticeSessionResponse(
                Guid.NewGuid(), "Ready", "BE", "vi", null, null, DateTime.UtcNow, null, []));

        var req = new CreateCampaignSessionInternalRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            new[] { "Q1" },
            new[] { new CampaignCriterionInput("Communication", null, 1.0m, 5) },
            RubricVersion: 4);

        await Controller(svc).CreateOrGetCampaignSession(req, "tok", CancellationToken.None);

        Assert.Equal(4, captured!.RubricVersion);
    }

    // ── (5) Bảng xếp hạng — điểm tổng phải theo bộ ĐÃ GHIM ──────────────────────────

    /// <summary>
    /// Buổi ghim v1 kết thúc SAU khi campaign đã bump v2 (v1 bị hạ cờ).
    ///
    /// Nếu điểm tổng đọc bộ tiêu chí theo <c>is_active</c> thì nó nạp v2, trong khi answer_scores mang
    /// criterion_id của v1 ⇒ hai tập ID KHÔNG GIAO NHAU ⇒ mọi vòng lặp `continue` ⇒ weightSum = 0 ⇒
    /// event mang TotalScore = 0 ⇒ ứng viên bị XẾP HẠNG BẰNG ĐIỂM 0 trong khi bài của họ đã được chấm
    /// đầy đủ. Đây là hồi quy mà chính việc ghim phiên bản tạo ra, nếu quên sửa chỗ tính điểm tổng.
    /// </summary>
    [Fact]
    public async Task ScoredEvent_SessionPinnedToOldVersion_ScoresWithPinnedRubric_NotZero()
    {
        using var t = new TestDb();
        var (campaignId, v1, _) = SeedTwoVersions(t);

        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, campaignId: campaignId);
        session.CampaignRubricVersion = 1;
        t.Db.Add(session);
        var question = TestDb.Question(session.Id);
        t.Db.Add(question);
        var answer = TestDb.Answer(session.Id, question.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.Add(answer);
        // Điểm đã chấm gắn với tiêu chí của bộ v1 (bộ đã bị hạ cờ).
        foreach (var c in v1)
            t.Db.AnswerScores.Add(new AnswerScore
            {
                AnswerId = answer.Id, CriterionId = c.Id, Score = 4m, RubricVersion = 1
            });
        await t.Db.SaveChangesAsync();

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(session.Id);
        await t.Db.SaveChangesAsync();

        var evt = TestDb.ScoredOutbox(t.NewContext(), session.Id);
        Assert.NotNull(evt);
        Assert.Equal(80m, evt!.TotalScore);   // 4/5 = 80%, KHÔNG phải 0
        Assert.Equal(1, evt.RubricVersion);   // nhãn thước đo cho bảng xếp hạng (CAMP-10)
    }

    // B2C không có thước đo campaign ⇒ nhãn phải là null. ⚠ null nghĩa "KHÔNG BIẾT/không áp dụng" —
    // đừng vẽ thành v1 ở tầng hiển thị (BK23: suy "biết" từ "không biết" là bịa).
    [Fact]
    public async Task ScoredEvent_B2CSession_HasNullRubricVersionLabel()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(session.Id);
        await t.Db.SaveChangesAsync();

        Assert.Null(TestDb.ScoredOutbox(t.NewContext(), session.Id)!.RubricVersion);
    }

    // ── (6) Republisher — đường cứu answer kẹt phải dùng CÙNG thước đo ──────────────

    /// <summary>
    /// Answer được cứu bằng republisher phải chấm bằng đúng bộ đã ghim. Lệch ở đây là cùng một answer
    /// sinh HAI <c>rubric_version</c> khác nhau ⇒ <c>attemptsForVersion</c> không bao giờ đủ N ⇒
    /// answer kẹt <c>Scoring</c> VĨNH VIỄN. Đúng chỗ F11 và đáp án mẫu đã dính.
    /// </summary>
    [Fact]
    public async Task Republisher_UsesPinnedVersion_NotLatestActiveSet()
    {
        using var t = new TestDb();
        var (campaignId, v1, v2) = SeedTwoVersions(t);

        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress, campaignId: campaignId);
        session.CampaignRubricVersion = 1;
        t.Db.Add(session);
        var question = TestDb.Question(session.Id);
        t.Db.Add(question);
        // -10': đã quá grace publish-hụt (2') nhưng CHƯA quá trần bỏ cuộc `Scoring:GiveUpAfterMinutes`
        // (20' từ 2026-08-20) — quá trần thì republisher thôi đẩy, test mất hiệu lực trong im lặng.
        t.Db.PracticeAnswers.Add(TestDb.Answer(
            session.Id, question.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-10), lastPublished: null));
        await t.Db.SaveChangesAsync();

        var job = await RepublishAndCapture(t);

        Assert.Equal(1, job.RubricVersion);
        Assert.All(job.Criteria, c => Assert.Contains(v1, x => x.Id == c.CriterionId));
        Assert.All(job.Criteria, c => Assert.DoesNotContain(v2, x => x.Id == c.CriterionId));
    }

    private static async Task<ScoringJob> RepublishAndCapture(TestDb t)
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
}
