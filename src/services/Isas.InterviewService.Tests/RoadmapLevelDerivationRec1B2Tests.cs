using System.Reflection;
using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// REC1-B2 — hai phạm vi mức KHÔNG được gộp làm một:
/// (A) mức LỘ TRÌNH suy từ buổi nguồn (<c>RoadmapService.CreateAsync</c>, thay lời tự khai
///     <c>req.Level</c> chưa ai hiệu chuẩn — đo trên production: chỉ 4/61 buổi đạt ngưỡng cấp của
///     chính mình);
/// (B) mức BÀI HỌC lấy từ chính các lỗi bài đó bám (<c>RoadmapLessonService.ResolveLessonSeniorityAsync</c>),
///     né cả âm tính giả (lộ trình Senior ôn lỗi Junior ở tầm Senior) lẫn dương tính giả (lấy MIN).
/// </summary>
public class RoadmapLevelDerivationRec1B2Tests
{
    private static int _orderCounter;

    // ═══════════════════════ Mục A — mức LỘ TRÌNH ═══════════════════════

    private static Mock<IAiServiceRoadmapGenerator> GenMock()
    {
        var m = new Mock<IAiServiceRoadmapGenerator>();
        m.Setup(x => x.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .ReturnsAsync(new RoadmapGenAiResult(new List<GeneratedMilestone>
            {
                new("M1", new List<string> { "Clarity" }, new List<GeneratedLesson> { new("L1") })
            }));
        return m;
    }

    /// <summary>Seed 1 buổi Scored + 1 SessionCriterionScore yếu + đúng 1 content mistake (Guard 3),
    /// với <paramref name="seniority"/> TUỲ CHỌN (khác mặc định "Junior" của <see cref="TestDb.Session"/>).</summary>
    private static Guid AddSession(
        TestDb t, Guid candidateId, RubricCriterion criterion, string seniority, DateTime createdAt)
    {
        if (t.Db.RubricCriteria.Local.All(c => c.Id != criterion.Id)
            && !t.Db.RubricCriteria.Any(c => c.Id == criterion.Id))
            t.Db.RubricCriteria.Add(criterion);

        var session = TestDb.Session(candidateId, SessionStatus.Scored, JobCategory.BE, createdAt: createdAt);
        session.Seniority = seniority;
        t.Db.PracticeSessions.Add(session);

        t.Db.SessionCriterionScores.Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            CriterionId = criterion.Id,
            CriterionName = criterion.Name,
            AverageScore = 1m,
            MaxScore = criterion.MaxScore,
            Percentage = 20m,
            Weight = 1m,
            NeedsImprovement = true,
            CreatedAt = createdAt
        });

        var question = TestDb.Question(session.Id, order: ++_orderCounter);
        t.Db.PracticeQuestions.Add(question);
        var answer = TestDb.Answer(session.Id, question.Id, AnswerStatus.Scored, createdAt, createdAt);
        answer.Transcript = "Câu trả lời của ứng viên cho " + criterion.Name;
        t.Db.PracticeAnswers.Add(answer);
        t.Db.AnswerScores.Add(new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = criterion.Id,
            AttemptNo = 1,
            Score = 1,
            Reasoning = $"Chưa nắm vững {criterion.Name}.",
            RubricVersion = 1,
            CreatedAt = createdAt
        });

        return session.Id;
    }

    /// <summary>
    /// Bullet 1 — 2 buổi Junior + Senior ⇒ roadmap.Level = Senior (mức CAO NHẤT), bất kể client
    /// gửi gì (ở đây cố tình gửi Fresher — giá trị thứ BA, không trùng buổi nào — để loại trừ khả
    /// năng "tình cờ đúng" nếu code lỡ đọc `req.Level`).
    /// </summary>
    [Fact]
    public async Task Create_HaiBuoiJuniorVaSenior_RoadmapLevelLaSenior()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var critJunior = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        var critSenior = TestDb.Criterion(JobCategory.BE, name: "Depth");
        var now = DateTime.UtcNow;

        var s1 = AddSession(t, candidateId, critJunior, "Junior", now.AddDays(-1));
        var s2 = AddSession(t, candidateId, critSenior, "Senior", now);
        await t.Db.SaveChangesAsync();

        var svc = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, GenMock().Object, NullLogger<RoadmapService>.Instance);

        await svc.CreateAsync(
            candidateId,
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Fresher, null, SessionIds: [s1, s2]),
            default);

        var roadmapRow = await t.NewContext().Roadmaps.AsNoTracking().SingleAsync();
        Assert.Equal(RoadmapLevel.Senior, roadmapRow.Level);
    }

    /// <summary>Bullet 2(a) — sàn phòng thủ khi không suy được mức nào từ buổi nguồn là "Junior",
    /// khớp mặc định của chính <c>PracticeSession.Seniority</c>/<c>PracticeService.DefaultSeniority</c>
    /// — không phải một mốc bịa riêng cho lộ trình. Đọc bằng reflection vì hằng số là `private`
    /// (không đường công khai nào tới được ca "0 buổi": Guard 1 đã ép `req.SessionIds` luôn
    /// {Count:>0} trước khi chạm mã này).</summary>
    [Fact]
    public void DefaultRoadmapLevel_LaJunior()
    {
        var field = typeof(RoadmapService).GetField(
            "DefaultRoadmapLevel", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(RoadmapLevel.Junior, (RoadmapLevel)field.GetValue(null)!);
    }

    /// <summary>
    /// Bullet 2(b) — 1 buổi Seniority=Junior, client GỬI KÈM <c>RoadmapLevel.Senior</c> ⇒ roadmap
    /// VẪN suy ra Junior (từ buổi), KHÔNG dùng giá trị client gửi.
    /// </summary>
    [Fact]
    public async Task Create_ClientGuiSenior_ButBuoiLaJunior_VanSuyJunior_KhongDungGiaTriClient()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        var sid = AddSession(t, candidateId, crit, "Junior", DateTime.UtcNow);
        await t.Db.SaveChangesAsync();

        var svc = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, GenMock().Object, NullLogger<RoadmapService>.Instance);

        await svc.CreateAsync(
            candidateId,
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Senior, null, SessionIds: [sid]),
            default);

        var roadmapRow = await t.NewContext().Roadmaps.AsNoTracking().SingleAsync();
        Assert.Equal(RoadmapLevel.Junior, roadmapRow.Level);
    }

    // ═══════════════════════ Mục B — mức BÀI HỌC ═══════════════════════

    private static (Mock<IPracticeService> practice, Func<CreatePracticeSessionRequest?> captured)
        CapturingPractice(TestDb t)
    {
        CreatePracticeSessionRequest? captured = null;
        var practice = new Mock<IPracticeService>();
        practice.Setup(p => p.CreateLessonSessionAsync(
                It.IsAny<Guid>(), It.IsAny<CreatePracticeSessionRequest>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<LessonContext?>(),
                It.IsAny<CancellationToken>()))
            .Callback((Guid cid, CreatePracticeSessionRequest req, Guid sid,
                       IReadOnlyList<string>? _, LessonContext? _, CancellationToken _) =>
            {
                captured = req;
                // Link lesson sau đó chạy FK roadmap_lessons.session_id (SQLite CÓ enforce FK trong
                // EF10) — mẫu RoadmapLessonTests.cs/EvidenceDrivenPr160Tests.cs.
                var s = TestDb.Session(cid, SessionStatus.Ready);
                s.Id = sid;
                t.Db.PracticeSessions.Add(s);
                t.Db.SaveChanges();
            })
            .ReturnsAsync(new PracticeSessionResponse(
                Guid.NewGuid(), "Ready", "BE", "vi", null, null, DateTime.UtcNow, null, []));
        return (practice, () => captured);
    }

    private static RubricCriterion SeedCriterion(TestDb t, string name = "Clarity")
    {
        var c = TestDb.Criterion(JobCategory.BE, name: name);
        t.Db.RubricCriteria.Add(c);
        return c;
    }

    private static RoadmapMistake Mistake(
        string key, Guid roadmapId, Guid criterionId, string? seniority, Guid? answerId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            RoadmapId = roadmapId,
            MistakeKey = key,
            CriterionId = criterionId,
            CriterionName = "Clarity",
            AnswerId = answerId,
            Question = "Câu hỏi?",
            Answer = "Câu trả lời.",
            Reasoning = "Lý do sai.",
            ScorePct = 20m,
            ThresholdPct = 50m,
            Seniority = seniority,
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>
    /// Bullet 3 — lộ trình Level=Senior, nhưng bài học BÁM lỗi được trích từ một buổi Junior (snapshot
    /// <c>RoadmapMistake.Seniority="Junior"</c>) ⇒ buổi ÔN cho bài đó phải Seniority=Junior, KHÔNG
    /// PHẢI Senior của roadmap. Đây chính là ca "âm tính giả" nêu trong tài liệu: ôn lỗi Junior ở
    /// tầm Senior sẽ ra câu khó hơn chỗ đã sai.
    /// </summary>
    [Fact]
    public async Task StartLesson_LoTrinhSenior_BaiBamLoiTuBuoiJunior_BuoiOnLaJunior()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var crit = SeedCriterion(t);
        var lesson = new RoadmapLesson { Id = Guid.NewGuid(), OrderNo = 1, Title = "L1", Status = LessonStatus.Theory, MistakeRefs = ["m1"] };
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = candidate, JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Senior,   // mức LỘ TRÌNH — KHÔNG được dùng cho bài này
            Status = RoadmapStatus.Active, CreatedAt = DateTime.UtcNow,
            Milestones = [new RoadmapMilestone
            {
                Id = Guid.NewGuid(), OrderNo = 1, Title = "M1", FocusCriteria = ["Clarity"],
                Status = MilestoneStatus.Pending, Lessons = [lesson]
            }]
        };
        t.Db.Roadmaps.Add(roadmap);
        t.Db.RoadmapMistakes.Add(Mistake("m1", roadmap.Id, crit.Id, seniority: "Junior"));
        await t.Db.SaveChangesAsync();

        var (practice, captured) = CapturingPractice(t);
        var svc = new RoadmapLessonService(
            t.Db, practice.Object, new Mock<IAiServiceRoadmapGenerator>().Object, NullLogger<RoadmapLessonService>.Instance);

        await svc.StartLessonAsync(candidate, roadmap.Id, lesson.Id);

        Assert.NotNull(captured());
        Assert.Equal("Junior", captured()!.Seniority);
    }

    /// <summary>
    /// Bullet 4 — bài bám 2 lỗi LỆCH MỨC (Junior + Senior) ⇒ lấy mức CAO NHẤT (Senior), không phải
    /// thấp nhất (Junior) và không phải trung bình/lùi roadmap.Level (đặt Middle để phân biệt cả
    /// ba khả năng: nếu code lỡ lùi roadmap.Level, kết quả sẽ là "Middle", không phải "Senior").
    /// </summary>
    [Fact]
    public async Task StartLesson_LoiLechMucTrongCungBai_LayCaoNhat()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var crit = SeedCriterion(t);
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(), OrderNo = 1, Title = "L1", Status = LessonStatus.Theory,
            MistakeRefs = ["m1", "m2"]
        };
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = candidate, JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Middle,   // KHÁC cả Junior lẫn Senior — phát hiện được nhánh lùi sai
            Status = RoadmapStatus.Active, CreatedAt = DateTime.UtcNow,
            Milestones = [new RoadmapMilestone
            {
                Id = Guid.NewGuid(), OrderNo = 1, Title = "M1", FocusCriteria = ["Clarity"],
                Status = MilestoneStatus.Pending, Lessons = [lesson]
            }]
        };
        t.Db.Roadmaps.Add(roadmap);
        t.Db.RoadmapMistakes.Add(Mistake("m1", roadmap.Id, crit.Id, seniority: "Junior"));
        t.Db.RoadmapMistakes.Add(Mistake("m2", roadmap.Id, crit.Id, seniority: "Senior"));
        await t.Db.SaveChangesAsync();

        var (practice, captured) = CapturingPractice(t);
        var svc = new RoadmapLessonService(
            t.Db, practice.Object, new Mock<IAiServiceRoadmapGenerator>().Object, NullLogger<RoadmapLessonService>.Instance);

        await svc.StartLessonAsync(candidate, roadmap.Id, lesson.Id);

        Assert.Equal("Senior", captured()!.Seniority);
    }

    /// <summary>
    /// Bullet 5 — hai ca đều phải LÙI về roadmap.Level: (a) bài không bám lỗi nào (MistakeRefs rỗng
    /// ở cả lesson lẫn milestone); (b) bài bám lỗi nhưng lỗi đó là hàng CŨ (Seniority=null, tạo
    /// trước migration này).
    /// </summary>
    [Fact]
    public async Task StartLesson_KhongBamLoiHoacLoiCuKhongCoSeniority_LuiVeRoadmapLevel()
    {
        using var t = new TestDb();
        var candidateA = Guid.NewGuid();
        var candidateB = Guid.NewGuid();
        var crit = SeedCriterion(t);

        // (a) không bám lỗi nào.
        var lessonA = new RoadmapLesson { Id = Guid.NewGuid(), OrderNo = 1, Title = "LA", Status = LessonStatus.Theory };
        var roadmapA = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = candidateA, JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Middle, Status = RoadmapStatus.Active, CreatedAt = DateTime.UtcNow,
            Milestones = [new RoadmapMilestone
            {
                Id = Guid.NewGuid(), OrderNo = 1, Title = "MA", FocusCriteria = ["Clarity"],
                Status = MilestoneStatus.Pending, Lessons = [lessonA]
            }]
        };

        // (b) bám lỗi "m1" nhưng Seniority=null (hàng cũ).
        var lessonB = new RoadmapLesson { Id = Guid.NewGuid(), OrderNo = 1, Title = "LB", Status = LessonStatus.Theory, MistakeRefs = ["m1"] };
        var roadmapB = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = candidateB, JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Fresher, Status = RoadmapStatus.Active, CreatedAt = DateTime.UtcNow,
            Milestones = [new RoadmapMilestone
            {
                Id = Guid.NewGuid(), OrderNo = 1, Title = "MB", FocusCriteria = ["Clarity"],
                Status = MilestoneStatus.Pending, Lessons = [lessonB]
            }]
        };

        t.Db.Roadmaps.AddRange(roadmapA, roadmapB);
        t.Db.RoadmapMistakes.Add(Mistake("m1", roadmapB.Id, crit.Id, seniority: null));
        await t.Db.SaveChangesAsync();

        var (practiceA, capturedA) = CapturingPractice(t);
        await new RoadmapLessonService(
            t.Db, practiceA.Object, new Mock<IAiServiceRoadmapGenerator>().Object, NullLogger<RoadmapLessonService>.Instance)
            .StartLessonAsync(candidateA, roadmapA.Id, lessonA.Id);
        Assert.Equal("Middle", capturedA()!.Seniority);

        var (practiceB, capturedB) = CapturingPractice(t);
        await new RoadmapLessonService(
            t.Db, practiceB.Object, new Mock<IAiServiceRoadmapGenerator>().Object, NullLogger<RoadmapLessonService>.Instance)
            .StartLessonAsync(candidateB, roadmapB.Id, lessonB.Id);
        Assert.Equal("Fresher", capturedB()!.Seniority);
    }

    /// <summary>
    /// Bullet 6 — answer GỐC bị xoá (FK <c>RoadmapMistake.AnswerId</c> SetNull) ⇒ mức bài học VẪN
    /// đọc được ĐÚNG, vì <c>Seniority</c> là SNAPSHOT lúc trích, không phải join qua answer→session
    /// lúc đọc. roadmap.Level cố tình đặt KHÁC (Middle) để phân biệt "đọc đúng snapshot Senior" với
    /// "lùi nhầm về roadmap.Level vì join hụt".
    /// </summary>
    [Fact]
    public async Task StartLesson_AnswerGocBiXoa_MucVanDocDuocTuSnapshot()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var crit = SeedCriterion(t);

        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.Seniority = "Senior";
        t.Db.PracticeSessions.Add(session);
        var question = TestDb.Question(session.Id, order: 1);
        t.Db.PracticeQuestions.Add(question);
        var answer = TestDb.Answer(session.Id, question.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.PracticeAnswers.Add(answer);

        var lesson = new RoadmapLesson { Id = Guid.NewGuid(), OrderNo = 1, Title = "L1", Status = LessonStatus.Theory, MistakeRefs = ["m1"] };
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = candidate, JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Middle,   // KHÁC "Senior" — phát hiện được nhánh lùi nhầm
            Status = RoadmapStatus.Active, CreatedAt = DateTime.UtcNow,
            Milestones = [new RoadmapMilestone
            {
                Id = Guid.NewGuid(), OrderNo = 1, Title = "M1", FocusCriteria = ["Clarity"],
                Status = MilestoneStatus.Pending, Lessons = [lesson]
            }]
        };
        t.Db.Roadmaps.Add(roadmap);
        // Seniority SNAPSHOT ngay lúc trích (mẫu RoadmapMistakeLoader) — KHÔNG đọc lại từ answer.
        t.Db.RoadmapMistakes.Add(Mistake("m1", roadmap.Id, crit.Id, seniority: "Senior", answerId: answer.Id));
        await t.Db.SaveChangesAsync();

        // Xoá ĐÚNG answer gốc — FK SetNull phải chạy, KHÔNG kéo sập roadmap_mistakes.
        t.Db.PracticeAnswers.Remove(await t.Db.PracticeAnswers.SingleAsync(a => a.Id == answer.Id));
        await t.Db.SaveChangesAsync();

        var mistakeAfterDelete = await t.NewContext().RoadmapMistakes.AsNoTracking().SingleAsync(m => m.MistakeKey == "m1");
        Assert.Null(mistakeAfterDelete.AnswerId);           // FK SetNull đã chạy thật
        Assert.Equal("Senior", mistakeAfterDelete.Seniority); // snapshot KHÔNG bị cuốn theo

        var (practice, captured) = CapturingPractice(t);
        var svc = new RoadmapLessonService(
            t.Db, practice.Object, new Mock<IAiServiceRoadmapGenerator>().Object, NullLogger<RoadmapLessonService>.Instance);

        await svc.StartLessonAsync(candidate, roadmap.Id, lesson.Id);

        Assert.Equal("Senior", captured()!.Seniority);
    }
}
