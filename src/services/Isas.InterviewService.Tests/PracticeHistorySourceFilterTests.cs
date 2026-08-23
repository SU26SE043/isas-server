using System.Security.Claims;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// <c>GET /practice/history</c> trộn chung buổi luyện TỰ DO với buổi sinh từ BÀI HỌC lộ trình mà
/// không có cách nào tách. Dữ liệu để phân biệt đã có sẵn (<c>roadmap_lesson_attempts</c>, và nhãn
/// <c>lessonTitle</c> đã lộ ra từ trước) — chỉ thiếu mỗi filter. Đo trên dev: một tài khoản có 3
/// buổi B2C, 1 tự do và 2 sinh từ bài học, hiện chung một danh sách.
///
/// <para><c>?source=</c> là OPT-IN: vắng ⇒ tập kết quả y hệt hôm nay.</para>
/// </summary>
public class PracticeHistorySourceFilterTests
{
    private static PracticeService BuildPractice(InterviewDbContext db)
        => new(db,
            new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

    /// <summary>Gắn một buổi vào một bài học (qua bảng LẦN LÀM) — đúng nguồn mà filter đọc.</summary>
    private static void AttachLesson(TestDb t, Guid owner, Guid sessionId, string title, int attemptNo = 1)
    {
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = owner,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(), RoadmapId = roadmap.Id, OrderNo = 1, Title = "Chặng 1"
        };
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(), MilestoneId = milestone.Id, OrderNo = 1, Title = title
        };
        t.Db.AddRange(roadmap, milestone, lesson);
        t.Db.RoadmapLessonAttempts.Add(new RoadmapLessonAttempt
        {
            Id = Guid.NewGuid(), LessonId = lesson.Id, SessionId = sessionId, AttemptNo = attemptNo
        });
    }

    /// 3 buổi: 1 tự do + 2 sinh từ bài học (đúng hình dạng dữ liệu đo được trên dev).
    private static async Task<(Guid free, Guid lessonA, Guid lessonB)> SeedMixed(TestDb t, Guid candidate)
    {
        var now = DateTime.UtcNow;
        var free = TestDb.Session(candidate, SessionStatus.Scored, createdAt: now);
        var lessonA = TestDb.Session(candidate, SessionStatus.Scored, createdAt: now.AddMinutes(-1));
        var lessonB = TestDb.Session(candidate, SessionStatus.InProgress, createdAt: now.AddMinutes(-2));
        t.Db.AddRange(free, lessonA, lessonB);
        await t.Db.SaveChangesAsync();

        AttachLesson(t, candidate, lessonA.Id, "Tổng quan OOP");
        AttachLesson(t, candidate, lessonB.Id, "Cấu trúc dữ liệu cơ bản");
        await t.Db.SaveChangesAsync();
        return (free.Id, lessonA.Id, lessonB.Id);
    }

    // ── (1) Vắng ⇒ hành vi cũ ────────────────────────────────────────────────────────────

    [Fact]
    public async Task KhongGuiSource_TraTatCa_GiongHanhViCu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate);

        Assert.Equal(3, page.Items.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SourceRong_CoiNhuKhongLoc(string? source)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, source: source);

        Assert.Equal(3, page.Items.Count);
    }

    // ── (2) Lọc đúng nhóm ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SourceLesson_ChiTraBuoiSinhTuBaiHoc()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (free, lessonA, lessonB) = await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, source: "lesson");

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, x => x.Id == lessonA);
        Assert.Contains(page.Items, x => x.Id == lessonB);
        Assert.DoesNotContain(page.Items, x => x.Id == free);
    }

    [Fact]
    public async Task SourceFree_ChiTraBuoiLuyenTuDo()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (free, lessonA, lessonB) = await SeedMixed(t, candidate);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, source: "free");

        Assert.Single(page.Items);
        Assert.Equal(free, page.Items[0].Id);
        Assert.DoesNotContain(page.Items, x => x.Id == lessonA || x.Id == lessonB);
    }

    /// <summary>
    /// PHÂN HOẠCH: <c>lesson</c> ∪ <c>free</c> = đúng tập khi không lọc, không thừa không thiếu.
    /// Đây là bất biến giữ cho người dùng bật tab nào cũng không bị mất buổi — nếu một trong hai
    /// nhánh ngầm loại thêm thứ gì (vd cho <c>free</c> tự loại buổi B2B) thì có buổi không nằm
    /// trong tab nào và không ai thấy nó biến mất.
    /// </summary>
    [Fact]
    public async Task LessonVaFree_LaPhanHoachCuaTapKhongLoc()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedMixed(t, candidate);
        // Thêm một buổi B2B để chứng minh nó KHÔNG rơi ra ngoài cả hai nhóm.
        var b2b = TestDb.Session(
            candidate, SessionStatus.Scored, campaignId: Guid.NewGuid(),
            createdAt: DateTime.UtcNow.AddMinutes(-3));
        t.Db.Add(b2b);
        await t.Db.SaveChangesAsync();

        var svc = BuildPractice(t.Db);
        var all = (await svc.GetHistoryAsync(candidate)).Items.Select(x => x.Id).ToHashSet();
        var lesson = (await svc.GetHistoryAsync(candidate, source: "lesson")).Items.Select(x => x.Id).ToList();
        var free = (await svc.GetHistoryAsync(candidate, source: "free")).Items.Select(x => x.Id).ToList();

        Assert.Empty(lesson.Intersect(free));
        Assert.Equal(all, lesson.Concat(free).ToHashSet());
        Assert.Contains(b2b.Id, free);   // B2B thuộc "không sinh từ bài học", không biến mất
    }

    /// Trực giao với <c>excludeCampaign</c>: ghép được để ra "tự do và chỉ B2C".
    [Fact]
    public async Task SourceFree_GhepExcludeCampaign_LoaiCaB2B()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (free, _, _) = await SeedMixed(t, candidate);
        t.Db.Add(TestDb.Session(
            candidate, SessionStatus.Scored, campaignId: Guid.NewGuid(),
            createdAt: DateTime.UtcNow.AddMinutes(-3)));
        await t.Db.SaveChangesAsync();

        var page = await BuildPractice(t.Db)
            .GetHistoryAsync(candidate, source: "free", excludeCampaign: true);

        Assert.Single(page.Items);
        Assert.Equal(free, page.Items[0].Id);
    }

    // ── (3) Lọc TRONG SQL, TRƯỚC khi cắt trang ───────────────────────────────────────────

    /// <summary>
    /// Buổi bài học DUY NHẤT nằm ở cuối danh sách chưa lọc; với <c>limit=1</c> nó rơi ra ngoài
    /// trang đầu. Lọc sau phân trang ⇒ trang đầu RỖNG và người dùng kết luận "không có buổi nào".
    /// Lọc trong SQL ⇒ nó là phần tử duy nhất của trang đầu.
    /// </summary>
    [Fact]
    public async Task LocTruocPhanTrang_BuoiNamNgoaiTrangDauVanRa()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // 3 buổi tự do MỚI hơn + 1 buổi bài học CŨ nhất (sắp xếp CreatedAt DESC ⇒ nó đứng cuối).
        for (var i = 0; i < 3; i++)
            t.Db.Add(TestDb.Session(candidate, SessionStatus.Scored, createdAt: now.AddMinutes(-i)));
        var old = TestDb.Session(candidate, SessionStatus.Scored, createdAt: now.AddMinutes(-30));
        t.Db.Add(old);
        await t.Db.SaveChangesAsync();
        AttachLesson(t, candidate, old.Id, "Bài nằm cuối danh sách");
        await t.Db.SaveChangesAsync();

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, limit: 1, source: "lesson");

        Assert.Single(page.Items);
        Assert.Equal(old.Id, page.Items[0].Id);
    }

    // ── (4) Giá trị lạ → 400, KHÔNG âm thầm bỏ filter (BK36) ─────────────────────────────

    [Theory]
    [InlineData("xyz")]
    [InlineData("Lesson")]    // sai hoa/thường — KHÔNG nhận (khuôn ValidateHistoryStatus)
    [InlineData("FREE")]
    [InlineData("roadmap")]
    [InlineData("1")]
    public async Task SourceLa_Nem400_KhongAmThamBoLoc(string source)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedMixed(t, candidate);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildPractice(t.Db).GetHistoryAsync(candidate, source: source));

        Assert.Contains("lesson", ex.Message);
        Assert.Contains("free", ex.Message);
        Assert.Contains(source, ex.Message);   // nói rõ giá trị đang gửi
    }

    /// <summary>
    /// Ném <c>InvalidOperationException</c> chỉ có nghĩa nếu controller map nó thành 400. Action
    /// <c>GetHistory</c> ĐÃ có nhánh bắt (thêm cùng lúc với <c>?status=</c>) — test này khoá lại để
    /// query param MỚI không lặng lẽ đi ra ngoài nhánh đó và biến giá trị lạ thành 500 (lớp lỗi F2b).
    /// </summary>
    [Fact]
    public async Task Controller_SourceGiaTriLa_Tra400_KhongPhai500()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedMixed(t, candidate);

        var ctrl = new PracticeController(BuildPractice(t.Db), Mock.Of<IQuestionSpeechService>(),
            NullLogger<PracticeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, candidate.ToString())], "test"))
                }
            }
        };

        var result = await ctrl.GetHistory(default, source: "roadmap");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// Controller phải THẬT SỰ chuyển `?source=` xuống service — khai param mà quên nối dây thì
    /// filter im lặng vô hiệu, HTTP vẫn 200 (đúng lớp bug đã cắn `MaxConcurrentInterviews`).
    [Fact]
    public async Task Controller_ChuyenSourceXuongService()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (free, _, _) = await SeedMixed(t, candidate);

        var ctrl = new PracticeController(BuildPractice(t.Db), Mock.Of<IQuestionSpeechService>(),
            NullLogger<PracticeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, candidate.ToString())], "test"))
                }
            }
        };

        var ok = Assert.IsType<OkObjectResult>(await ctrl.GetHistory(default, source: "free"));
        var items = Assert.IsAssignableFrom<IReadOnlyList<DTOs.PracticeSessionSummary>>(ok.Value);

        Assert.Single(items);
        Assert.Equal(free, items[0].Id);
    }

    // ── (5) Owner-scope vẫn nguyên (BC-3) ────────────────────────────────────────────────

    [Fact]
    public async Task SourceLesson_KhongLoBuoiCuaNguoiKhac()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var theirs = TestDb.Session(other, SessionStatus.Scored, createdAt: DateTime.UtcNow);
        t.Db.Add(theirs);
        await t.Db.SaveChangesAsync();
        AttachLesson(t, other, theirs.Id, "Bài của người khác");
        await t.Db.SaveChangesAsync();

        var page = await BuildPractice(t.Db).GetHistoryAsync(me, source: "lesson");

        Assert.Empty(page.Items);
    }
}
