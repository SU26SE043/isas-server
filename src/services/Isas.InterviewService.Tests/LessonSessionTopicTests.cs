using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Buổi luyện của một bài học phải mang theo CHỦ ĐỀ CỦA ĐÚNG BÀI ĐÓ, và KHÔNG mang CV của lộ trình.
///
/// <para><b>(1) Bám bài, không bám chặng.</b> <c>/start</c> trước đây chỉ gửi
/// <c>lesson.Milestone.FocusCriteria</c> — thuộc về CHẶNG — nên mọi bài cùng chặng cho lớp sinh
/// đúng một đầu vào (đo trên dev: 1 chặng/4 bài/cùng 3 tiêu chí; 2,8 bài/chặng trên 87 chặng).</para>
///
/// <para><b>(2) Không nhét CV.</b> CV chọn MỘT LẦN lúc lập lộ trình được nhét vào prompt của CẢ 14
/// bài. Đo trên dev: 2 lộ trình <c>BE</c> dùng CV mở đầu "NGUYEN VAN NAM - Business Analyst", và
/// một buổi của bài SQL nhận câu hỏi mở đầu "Với kinh nghiệm làm Business Analyst…". Không chặn
/// được bằng cách kiểm nghề: <c>file_records</c> không có cột nghề nào.</para>
/// </summary>
public class LessonSessionTopicTests
{
    /// 1 lộ trình / 1 chặng / 2 bài CÙNG chặng ⇒ cùng `FocusCriteria`, khác `Title`.
    private static Roadmap SeedRoadmap(TestDb t, Guid candidate, Guid? cvId = null, string? theory = null)
    {
        var lessons = new List<RoadmapLesson>
        {
            new() { Id = Guid.NewGuid(), OrderNo = 1, Title = "Tổng quan OOP",
                    Status = LessonStatus.Theory, TheoryContent = theory },
            new() { Id = Guid.NewGuid(), OrderNo = 2, Title = "Thuật toán tìm kiếm và sắp xếp",
                    Status = LessonStatus.Theory }
        };
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(), OrderNo = 1, Title = "Nền tảng Lập trình & Cấu trúc Dữ liệu",
            FocusCriteria = ["Chiều sâu kỹ thuật", "Giải quyết vấn đề & thuật toán"],
            Status = MilestoneStatus.Pending, Lessons = lessons
        };
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = candidate, JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior, CvId = cvId, Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow, Milestones = [milestone]
        };
        t.Db.Roadmaps.Add(roadmap);
        t.Db.SaveChanges();
        return roadmap;
    }

    private sealed record Captured(CreatePracticeSessionRequest Request, LessonContext? Lesson);

    private static (Mock<IPracticeService> practice, List<Captured> captured) CapturingPractice(TestDb t)
    {
        var captured = new List<Captured>();
        var practice = new Mock<IPracticeService>();
        practice.Setup(p => p.CreateLessonSessionAsync(
                It.IsAny<Guid>(), It.IsAny<CreatePracticeSessionRequest>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<LessonContext?>(),
                It.IsAny<CancellationToken>()))
            .Callback((Guid cid, CreatePracticeSessionRequest req, Guid sid,
                       IReadOnlyList<string>? _, LessonContext? lc, CancellationToken _) =>
            {
                captured.Add(new Captured(req, lc));
                // Row session THẬT: link lesson sau đó chạy FK roadmap_lessons.session_id.
                var s = TestDb.Session(cid, SessionStatus.Ready);
                s.Id = sid;
                t.Db.PracticeSessions.Add(s);
                t.Db.SaveChanges();
            })
            .ReturnsAsync(new PracticeSessionResponse(
                Guid.NewGuid(), "Ready", "BE", "vi", null, null, DateTime.UtcNow, null, []));
        return (practice, captured);
    }

    private static RoadmapLessonService Service(TestDb t, IPracticeService practice)
        => new(t.Db, practice, new Mock<IAiServiceRoadmapGenerator>().Object,
            NullLogger<RoadmapLessonService>.Instance,
            scoringOptions: null, roadmapOptions: Options.Create(new RoadmapOptions()));

    // ═══════════ (1) Chủ đề bám BÀI ═══════════

    [Fact]
    public async Task StartLesson_GuiTieuDeCuaDungBaiDo()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var lesson1 = r.Milestones.First().Lessons.First(l => l.OrderNo == 1);
        var (practice, captured) = CapturingPractice(t);

        await Service(t, practice.Object).StartLessonAsync(user, r.Id, lesson1.Id);

        Assert.Single(captured);
        Assert.NotNull(captured[0].Lesson);
        Assert.Equal("Tổng quan OOP", captured[0].Lesson!.Title);
    }

    /// <summary>
    /// 🔑 TEST TRỌNG TÂM — hai bài trong CÙNG một chặng phải cho hai ngữ cảnh KHÁC nhau.
    /// Đây chính xác là con bug: `FocusCriteria` của chúng giống hệt nhau, nên nếu chỉ có nó thì
    /// hai buổi nhận đúng một đầu vào và AI hỏi lẫn chủ đề của nhau.
    /// </summary>
    [Fact]
    public async Task HaiBaiCungChang_NgucCanhKhacNhau_DuFocusCriteriaGiongHet()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var lessons = r.Milestones.First().Lessons.OrderBy(l => l.OrderNo).ToList();
        var (practice, captured) = CapturingPractice(t);
        var svc = Service(t, practice.Object);

        await svc.StartLessonAsync(user, r.Id, lessons[0].Id);
        await svc.StartLessonAsync(user, r.Id, lessons[1].Id);

        Assert.Equal(2, captured.Count);
        Assert.NotEqual(captured[0].Lesson!.Title, captured[1].Lesson!.Title);
        Assert.Equal("Tổng quan OOP", captured[0].Lesson!.Title);
        Assert.Equal("Thuật toán tìm kiếm và sắp xếp", captured[1].Lesson!.Title);
    }

    /// Tiêu đề phải là của BÀI, không phải của CHẶNG (dễ lấy nhầm vì `Milestone` đã `.Include`).
    [Fact]
    public async Task TieuDeLaCuaBai_KhongPhaiCuaChang()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var lesson1 = r.Milestones.First().Lessons.First(l => l.OrderNo == 1);
        var (practice, captured) = CapturingPractice(t);

        await Service(t, practice.Object).StartLessonAsync(user, r.Id, lesson1.Id);

        Assert.NotEqual("Nền tảng Lập trình & Cấu trúc Dữ liệu", captured[0].Lesson!.Title);
    }

    [Fact]
    public async Task CoBaiGiang_GuiKemMucLuc()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user, theory: "# T\n\n## Đóng gói\n\nabc\n\n## Kế thừa\n\ndef");
        var lesson1 = r.Milestones.First().Lessons.First(l => l.OrderNo == 1);
        var (practice, captured) = CapturingPractice(t);

        await Service(t, practice.Object).StartLessonAsync(user, r.Id, lesson1.Id);

        Assert.Equal("Đóng gói\nKế thừa", captured[0].Lesson!.Outline);
    }

    /// Bấm "Bắt đầu" mà chưa mở bài lần nào — hợp lệ (lý thuyết sinh lazy), chỉ mất lớp mục lục.
    [Fact]
    public async Task ChuaCoBaiGiang_MucLucNull_VanCoTieuDe()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var lesson1 = r.Milestones.First().Lessons.First(l => l.OrderNo == 1);
        var (practice, captured) = CapturingPractice(t);

        await Service(t, practice.Object).StartLessonAsync(user, r.Id, lesson1.Id);

        Assert.Null(captured[0].Lesson!.Outline);
        Assert.Equal("Tổng quan OOP", captured[0].Lesson!.Title);
    }

    /// Làm lại bài (endpoint RIÊNG) đi qua cùng thân — không được rơi mất chủ đề.
    [Fact]
    public async Task RetryLesson_CungMangChuDe()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var lesson1 = r.Milestones.First().Lessons.First(l => l.OrderNo == 1);
        await t.Db.RoadmapLessons.Where(l => l.Id == lesson1.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LessonStatus.Done));
        var (practice, captured) = CapturingPractice(t);

        await Service(t, practice.Object).RetryLessonAsync(user, r.Id, lesson1.Id);

        Assert.Equal("Tổng quan OOP", captured[0].Lesson!.Title);
    }

    // ═══════════ (1b) PracticeService phải CHUYỂN TIẾP chủ đề xuống lớp sinh ═══════════

    /// <summary>
    /// Mắt xích ở GIỮA: <c>RoadmapLessonService</c> dựng đúng ngữ cảnh mà <c>PracticeService</c>
    /// nuốt mất thì tính năng vẫn hỏng y nguyên — và hai bộ test ở hai đầu (dựng ngữ cảnh · payload
    /// HTTP) đều vẫn XANH. Dùng <c>PracticeService</c> THẬT, chỉ giả lớp sinh + reserve.
    /// </summary>
    [Fact]
    public async Task PracticeService_ChuyenChuDeXuongLopSinh()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        LessonContext? seen = null;

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<LessonContext>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string? _, string? _, IReadOnlyList<string>? _, int? _,
                       IReadOnlyList<GroundingChunk>? _, string _,
                       IReadOnlyList<QuestionTargetCriterionDto>? _, string _,
                       LessonContext lc, CancellationToken _) => seen = lc)
            .ReturnsAsync(new GeneratedQuestionsResult(
                [new GeneratedQuestion { Content = "Q1?" }], []));

        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                "User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var practice = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);

        await practice.CreateLessonSessionAsync(
            user,
            new CreatePracticeSessionRequest(
                CvId: null, JdId: null, JobCategory.BE, QuestionCount: 3),
            Guid.NewGuid(), ["Chiều sâu kỹ thuật"],
            new LessonContext("Tổng quan OOP", "Đóng gói"));

        Assert.NotNull(seen);
        Assert.Equal("Tổng quan OOP", seen!.Title);
        Assert.Equal("Đóng gói", seen.Outline);
    }

    // ═══════════ (2) KHÔNG nhét CV của lộ trình vào buổi bài học ═══════════

    [Fact]
    public async Task StartLesson_KhongGanCvCuaLoTrinh()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cv = new FileRecord
        {
            Id = Guid.NewGuid(), UserId = user, FileType = "CV", OriginalName = "cv.pdf",
            StoragePath = "k", StorageBucket = "b", MimeType = "application/pdf", FileSize = 1,
            ParsedText = "NGUYEN VAN NAM - Business Analyst", ParseStatus = "Parsed",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        t.Db.Add(cv);
        await t.Db.SaveChangesAsync();
        var r = SeedRoadmap(t, user, cvId: cv.Id);
        var lesson1 = r.Milestones.First().Lessons.First(l => l.OrderNo == 1);
        var (practice, captured) = CapturingPractice(t);

        await Service(t, practice.Object).StartLessonAsync(user, r.Id, lesson1.Id);

        // Lộ trình VẪN giữ cv_id (provenance + kiểm quyền lúc tạo); chỉ buổi luyện không mang nó.
        Assert.Equal(cv.Id, (await t.Db.Roadmaps.FindAsync(r.Id))!.CvId);
        Assert.Null(captured[0].Request.CvId);
    }
}
