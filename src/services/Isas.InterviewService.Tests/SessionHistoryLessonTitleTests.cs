using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Bảng "chọn báo cáo phỏng vấn" của wizard lộ trình đọc <c>GET /practice/sessions/history</c>,
/// nhưng <c>practice_sessions</c> KHÔNG có cột tên/tiêu đề nào ⇒ trước bản này client chỉ có
/// <c>jobCategory</c> để hiển thị và mọi buổi Backend hiện đúng một chữ "BE". Đo trên dev: 8 buổi
/// <c>BE|Junior</c> liên tiếp của cùng một người, không dòng nào phân biệt được với dòng nào.
///
/// <para>Nhãn lấy từ dữ liệu THẬT — <c>roadmap_lesson_attempts</c> (UNIQUE <c>session_id</c> ⇒ ghép
/// 1–1) → <c>roadmap_lessons.title</c>. Buổi luyện tự do không có nhãn nào trong hệ thống, và câu
/// trả lời đúng cho nhóm đó là <c>null</c>, KHÔNG phải một cái tên tự dựng.</para>
/// </summary>
public class SessionHistoryLessonTitleTests
{
    private static PracticeService BuildPractice(InterviewDbContext db)
        => new(db,
            new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

    /// <summary>Dựng cây roadmap → milestone → lesson, trả về lesson để gắn buổi luyện.</summary>
    private static RoadmapLesson SeedLesson(TestDb t, Guid owner, string title)
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
            Id = Guid.NewGuid(),
            RoadmapId = roadmap.Id,
            OrderNo = 1,
            Title = "Chặng 1"
        };
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(),
            MilestoneId = milestone.Id,
            OrderNo = 1,
            Title = title
        };
        t.Db.AddRange(roadmap, milestone, lesson);
        return lesson;
    }

    private static PracticeSession SeedSession(TestDb t, Guid owner, DateTime? createdAt = null)
    {
        var s = TestDb.Session(owner, SessionStatus.Scored, createdAt: createdAt);
        t.Db.Add(s);
        return s;
    }

    private static void Link(TestDb t, RoadmapLesson lesson, PracticeSession session, int attemptNo)
        => t.Db.Add(new RoadmapLessonAttempt
        {
            LessonId = lesson.Id,
            SessionId = session.Id,
            AttemptNo = attemptNo,
            CreatedAt = session.CreatedAt
        });

    // ── (1) Buổi sinh ra từ một bài học → mang đúng tên bài đó ────────────────────────────
    [Fact]
    public async Task BuoiThuocBaiHoc_TraDungTenBai()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        const string title = "Truy vấn SQL nâng cao (JOIN, GROUP BY) và cơ chế Index cơ bản";
        var lesson = SeedLesson(t, user, title);
        var session = SeedSession(t, user);
        Link(t, lesson, session, 1);
        await t.Db.SaveChangesAsync();

        var page = await BuildPractice(t.Db).GetHistoryAsync(user);

        Assert.Equal(title, Assert.Single(page.Items).LessonTitle);
    }

    // ── (2) Buổi luyện TỰ DO → null. Không có nhãn thì nói KHÔNG CÓ, không bịa tên ───────
    [Fact]
    public async Task BuoiLuyenTuDo_TraNull_KhongBiaTen()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        SeedSession(t, user);
        await t.Db.SaveChangesAsync();

        var page = await BuildPractice(t.Db).GetHistoryAsync(user);

        Assert.Null(Assert.Single(page.Items).LessonTitle);
    }

    // ── (3) CA QUYẾT ĐỊNH: bài đã luyện LẠI thì buổi CŨ vẫn phải có nhãn ─────────────────
    //
    // `roadmap_lessons.session_id` là quan hệ 1–1 chỉ trỏ buổi MỚI NHẤT, nên ghép qua cột đó sẽ làm
    // mọi buổi của lần làm trước rơi về null — đúng nhóm buổi mà người học cần chọn khi so tiến bộ.
    // Đo trên dev: đang có 1 buổi rơi vào ca này. Ghép qua bảng LẦN LÀM thì cả hai buổi đều có nhãn.
    [Fact]
    public async Task BaiDaLuyenLai_BuoiCu_VanCoNhan()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        const string title = "Xây dựng API RESTful cơ bản (CRUD, validation, xử lý lỗi)";
        var lesson = SeedLesson(t, user, title);

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lanDau = SeedSession(t, user, t0);
        var lamLai = SeedSession(t, user, t0.AddDays(1));
        Link(t, lesson, lanDau, 1);
        Link(t, lesson, lamLai, 2);

        // Đúng như production sau khi làm lại: cột 1–1 chỉ còn trỏ buổi mới nhất.
        lesson.SessionId = lamLai.Id;
        await t.Db.SaveChangesAsync();

        var page = await BuildPractice(t.Db).GetHistoryAsync(user);

        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, x => Assert.Equal(title, x.LessonTitle));
    }

    // ── (4) Nhãn phải gắn ĐÚNG buổi của nó — đây chính là điều bảng chọn cần ─────────────
    [Fact]
    public async Task HaiBuoiHaiBai_MoiBuoiMangNhanCuaChinhNo()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var baiSql = SeedLesson(t, user, "Truy vấn SQL nâng cao");
        var baiApi = SeedLesson(t, user, "Thiết kế API cho tính năng CRUD");
        var buoiSql = SeedSession(t, user, t0);
        var buoiApi = SeedSession(t, user, t0.AddHours(1));
        var buoiTuDo = SeedSession(t, user, t0.AddHours(2));
        Link(t, baiSql, buoiSql, 1);
        Link(t, baiApi, buoiApi, 1);
        await t.Db.SaveChangesAsync();

        var page = await BuildPractice(t.Db).GetHistoryAsync(user);

        Assert.Equal("Truy vấn SQL nâng cao", page.Items.Single(x => x.Id == buoiSql.Id).LessonTitle);
        Assert.Equal("Thiết kế API cho tính năng CRUD", page.Items.Single(x => x.Id == buoiApi.Id).LessonTitle);
        Assert.Null(page.Items.Single(x => x.Id == buoiTuDo.Id).LessonTitle);
    }

    // ── (5) Nhãn KHÔNG rò sang người khác (endpoint vốn owner-scoped — khoá lại cho chắc) ─
    [Fact]
    public async Task BuoiCuaNguoiKhac_KhongLotVaoLichSu()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var nguoiKhac = Guid.NewGuid();
        var lesson = SeedLesson(t, nguoiKhac, "Bài của người khác");
        var buoiHo = SeedSession(t, nguoiKhac);
        Link(t, lesson, buoiHo, 1);
        SeedSession(t, user);
        await t.Db.SaveChangesAsync();

        var page = await BuildPractice(t.Db).GetHistoryAsync(user);

        Assert.Null(Assert.Single(page.Items).LessonTitle);
    }
}
