using System.Security.Claims;
using System.Text.Json;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// LÀM LẠI một bài luyện trong lộ trình để nâng điểm: tốn 1 credit như mọi buổi khác, câu hỏi sinh
/// mới, giữ TRỌN lịch sử các lần làm (<c>roadmap_lesson_attempts</c>), và mở lại lộ trình đã đóng
/// sổ để báo cáo cuối được tính lại với số mới.
/// </summary>
public class RoadmapLessonRetryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Seed / dàn dựng ─────────────────────────────────────────────────────────────────
    private static Roadmap SeedRoadmap(
        TestDb t, Guid candidateId,
        RoadmapStatus roadmapStatus = RoadmapStatus.Active,
        MilestoneStatus mileStatus = MilestoneStatus.Pending)
    {
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            Status = roadmapStatus,
            CreatedAt = DateTime.UtcNow,
            Milestones =
            {
                new RoadmapMilestone
                {
                    Id = Guid.NewGuid(),
                    OrderNo = 1,
                    Title = "Milestone 1",
                    FocusCriteria = ["Clarity", "Depth"],
                    Status = mileStatus,
                    Lessons =
                    {
                        new RoadmapLesson
                        {
                            Id = Guid.NewGuid(), OrderNo = 1, Title = "Lesson 1",
                            Status = LessonStatus.Theory
                        }
                    }
                }
            }
        };
        t.Db.Roadmaps.Add(roadmap);
        t.Db.SaveChanges();
        return roadmap;
    }

    private static (Guid roadmapId, Guid milestoneId, Guid lessonId) Ids(Roadmap r)
    {
        var m = r.Milestones.First();
        return (r.Id, m.Id, m.Lessons.First().Id);
    }

    private static PracticeService RealPractice(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, Mock<ICreditReservationClient> reservation)
        => new(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);

    private static RoadmapsController Controller(TestDb t, IPracticeService practice, Guid userId)
    {
        var lessonService = new RoadmapLessonService(
            t.Db, practice, new Mock<IAiServiceRoadmapGenerator>().Object,
            NullLogger<RoadmapLessonService>.Instance);
        var controller = new RoadmapsController(
            new Mock<IRoadmapService>().Object, lessonService,
            new Mock<IRoadmapReportService>().Object, NullLogger<RoadmapsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            }
        };
        return controller;
    }

    private static Mock<IAiServiceQuestionGenerator> QuestionGenOk()
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        // Đường BÀI HỌC nay đi overload mang `lessonContext` (11 tham số) — thiếu setup này thì Moq
        // trả `null` và mọi test /start, /retry hỏng với "Sinh câu hỏi thất bại".
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<LessonContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                [new GeneratedQuestion { Content = "Q1" }, new GeneratedQuestion { Content = "Q2" }], []));
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" }, new() { Content = "Q2" } });
        return gen;
    }

    private static Mock<ICreditReservationClient> ReserveOk()
    {
        var m = new Mock<ICreditReservationClient>();
        m.Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return m;
    }

    /// <summary>Bấm Bắt đầu thật (đi qua đường production) rồi đánh dấu bài đã xong — tiền đề của
    /// mọi test làm lại. Trả về id buổi của LẦN ĐẦU.</summary>
    private static async Task<Guid> StartThenFinishAsync(
        TestDb t, RoadmapsController ctrl, Guid roadmapId, Guid lessonId)
    {
        var created = Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lessonId, default));
        var first = Assert.IsType<PracticeSessionResponse>(created.Value);

        await t.Db.RoadmapLessons.Where(l => l.Id == lessonId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LessonStatus.Done));
        return first.Id;
    }

    // ── (1) Bài ĐÃ XONG làm lại được: buổi MỚI, số lần tăng, lịch sử giữ nguyên ──────────
    [Fact]
    public async Task Retry_BaiDaXong_TaoBuoiMoi_VaTangSoLanLam()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, user));

        var reservation = ReserveOk();
        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), reservation), user);
        var firstSession = await StartThenFinishAsync(t, ctrl, roadmapId, lessonId);

        var ok = Assert.IsType<OkObjectResult>(await ctrl.RetryLesson(roadmapId, lessonId, default));
        var second = Assert.IsType<PracticeSessionResponse>(ok.Value);

        // Buổi MỚI, không phải buổi cũ — câu hỏi sinh lại chứ không chép.
        Assert.NotEqual(firstSession, second.Id);

        var db = t.NewContext();
        var attempts = await db.RoadmapLessonAttempts.AsNoTracking()
            .Where(a => a.LessonId == lessonId).OrderBy(a => a.AttemptNo).ToListAsync();
        Assert.Equal(2, attempts.Count);
        Assert.Equal([1, 2], attempts.Select(a => a.AttemptNo));
        // Lịch sử GIỮ: buổi lần đầu vẫn truy được qua bảng attempts dù lesson.session_id đã đổi.
        Assert.Equal(firstSession, attempts[0].SessionId);
        Assert.Equal(second.Id, attempts[1].SessionId);

        var lesson = await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lessonId);
        Assert.Equal(LessonStatus.Practicing, lesson.Status);
        Assert.Equal(second.Id, lesson.SessionId);          // trỏ lần MỚI NHẤT

        // Đúng 1 credit cho lần làm lại (tổng 2 lượt reserve cho 2 lần làm).
        reservation.Verify(x => x.ReserveAsync("User", user, second.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, await db.PracticeSessions.AsNoTracking().CountAsync());
    }

    // ── (2) Bấm Bắt đầu cũng phải ghi lại "lần thứ 1" ───────────────────────────────────
    // Thiếu vế này thì bài vừa học xong hiện `attemptCount = 0`, và lần làm lại kế tiếp lại được
    // cấp số 1 — số thứ tự mất nghĩa ngay từ lần thứ hai.
    [Fact]
    public async Task Start_GhiLaiLanLamThuNhat()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, user));
        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), ReserveOk()), user);

        var created = Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lessonId, default));
        var session = Assert.IsType<PracticeSessionResponse>(created.Value);

        var attempt = await t.NewContext().RoadmapLessonAttempts.AsNoTracking()
            .SingleAsync(a => a.LessonId == lessonId);
        Assert.Equal(1, attempt.AttemptNo);
        Assert.Equal(session.Id, attempt.SessionId);
    }

    // ── (3) Đang có buổi dở → 409, KHÔNG reserve thêm, KHÔNG mở buổi thứ hai ────────────
    [Fact]
    public async Task Retry_DangLuyen_Tra409_KhongReserveThem()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, user));

        var reservation = ReserveOk();
        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), reservation), user);
        // Bắt đầu nhưng KHÔNG hoàn thành → lesson đang Practicing.
        Assert.IsType<CreatedResult>(await ctrl.StartLesson(roadmapId, lessonId, default));
        reservation.Invocations.Clear();

        var conflict = Assert.IsType<ConflictObjectResult>(
            await ctrl.RetryLesson(roadmapId, lessonId, default));
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        reservation.Verify(x => x.ReserveAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, await t.NewContext().PracticeSessions.AsNoTracking().CountAsync());
        Assert.Equal(1, await t.NewContext().RoadmapLessonAttempts.AsNoTracking().CountAsync());
    }

    // ── (4) Chưa học lần nào → 409 (phải bấm Bắt đầu, không phải Làm lại) ───────────────
    [Fact]
    public async Task Retry_ConTheory_Tra409_KhongReserve()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, user));

        var reservation = ReserveOk();
        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), reservation), user);

        var conflict = Assert.IsType<ConflictObjectResult>(
            await ctrl.RetryLesson(roadmapId, lessonId, default));
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        reservation.Verify(x => x.ReserveAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await t.NewContext().PracticeSessions.AsNoTracking().AnyAsync());
    }

    // ── (5) Hết credit → 402 và KHÔNG tạo session (PAY-5) ──────────────────────────────
    // Guard trạng thái phải chạy TRƯỚC reserve, và reserve hỏng thì không được để lại buổi mồ côi.
    [Fact]
    public async Task Retry_HetCredit_Tra402_KhongTaoSession()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, user));

        var reservation = ReserveOk();
        var gen = QuestionGenOk();
        var ctrl = Controller(t, RealPractice(t, gen, reservation), user);
        var firstSession = await StartThenFinishAsync(t, ctrl, roadmapId, lessonId);

        // Từ đây ví hết credit.
        reservation.Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientCreditException("Ví không đủ credit"));
        gen.Invocations.Clear();

        var obj = Assert.IsType<ObjectResult>(await ctrl.RetryLesson(roadmapId, lessonId, default));
        Assert.Equal(StatusCodes.Status402PaymentRequired, obj.StatusCode);

        var db = t.NewContext();
        // KHÔNG buổi mới, bài vẫn Done, lịch sử không thêm dòng nào.
        Assert.Equal(1, await db.PracticeSessions.AsNoTracking().CountAsync());
        var lesson = await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lessonId);
        Assert.Equal(LessonStatus.Done, lesson.Status);
        Assert.Equal(firstSession, lesson.SessionId);
        Assert.Equal(1, await db.RoadmapLessonAttempts.AsNoTracking().CountAsync());

        // AI sinh câu hỏi KHÔNG được gọi — reserve chặn trước.
        // ⚠ Nhắm ĐÚNG overload mà đường bài học dùng: `Times.Never` trên overload cũ nay là assert
        // RỖNG NGHĨA (overload đó không còn được đường này gọi tới, nên luôn đúng).
        gen.Verify(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
            It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<LessonContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── (6) Lộ trình ĐÃ ĐÓNG → mở lại Active + xoá bản báo cáo chốt sổ ──────────────────
    // Không mở lại thì người học nâng điểm xong mà GET /report vẫn trả snapshot cũ ⇒ nút bấm vô nghĩa.
    [Fact]
    public async Task Retry_LoTrinhDaDong_MoLaiActive_VaXoaBaoCaoChot()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, user));

        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), ReserveOk()), user);
        await StartThenFinishAsync(t, ctrl, roadmapId, lessonId);

        // Lộ trình đã được BC15 đóng sổ.
        await t.Db.Roadmaps.Where(r => r.Id == roadmapId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, RoadmapStatus.Completed)
                .SetProperty(r => r.FinalReport, "{\"radar\":[]}")
                .SetProperty(r => r.OverallComment, "Bản chốt cũ")
                .SetProperty(r => r.CompletedAt, DateTime.UtcNow));

        Assert.IsType<OkObjectResult>(await ctrl.RetryLesson(roadmapId, lessonId, default));

        var roadmap = await t.NewContext().Roadmaps.AsNoTracking().FirstAsync(r => r.Id == roadmapId);
        Assert.Equal(RoadmapStatus.Active, roadmap.Status);
        Assert.Null(roadmap.FinalReport);
        Assert.Null(roadmap.OverallComment);
        Assert.Null(roadmap.CompletedAt);
    }

    // ── (7) Milestone ĐÃ hoàn thành KHÔNG bị hạ cấp khi một bài của nó quay lại Practicing ──
    [Fact]
    public async Task Retry_MilestoneDaHoanThanh_KhongBiHaCap()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, milestoneId, lessonId) = Ids(SeedRoadmap(t, user));

        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), ReserveOk()), user);
        await StartThenFinishAsync(t, ctrl, roadmapId, lessonId);

        await t.Db.RoadmapMilestones.Where(m => m.Id == milestoneId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.Status, MilestoneStatus.Completed)
                .SetProperty(m => m.CompletedAt, DateTime.UtcNow));

        Assert.IsType<OkObjectResult>(await ctrl.RetryLesson(roadmapId, lessonId, default));

        var mile = await t.NewContext().RoadmapMilestones.AsNoTracking().FirstAsync(m => m.Id == milestoneId);
        Assert.Equal(MilestoneStatus.Completed, mile.Status);
        Assert.NotNull(mile.CompletedAt);
    }

    // ── (8) Không phải lộ trình của mình → 404 ─────────────────────────────────────────
    [Fact]
    public async Task Retry_KhongPhaiChuSoHuu_Tra403()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, owner));

        var reservation = ReserveOk();
        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), reservation), Guid.NewGuid());

        var obj = Assert.IsType<ObjectResult>(await ctrl.RetryLesson(roadmapId, lessonId, default));
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        reservation.Verify(x => x.ReserveAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retry_LessonKhongTonTai_Tra404()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, _) = Ids(SeedRoadmap(t, user));
        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), ReserveOk()), user);

        Assert.IsType<NotFoundObjectResult>(
            await ctrl.RetryLesson(roadmapId, Guid.NewGuid(), default));
    }

    // ── (9) Mở bài trả về số lần đã làm + cờ được-làm-lại ──────────────────────────────
    [Fact]
    public async Task OpenLesson_TraSoLanLam_VaCoCanRetryTheoTrangThai()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, user));
        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), ReserveOk()), user);

        // Chưa làm lần nào → 0 lần, chưa được làm lại (phải bấm Bắt đầu).
        await t.Db.RoadmapLessons.Where(l => l.Id == lessonId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.TheoryContent, "## Bài\n\nNội dung."));
        var before = Assert.IsType<LessonResponse>(
            Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmapId, lessonId, default)).Value);
        Assert.Equal(0, before.AttemptCount);
        Assert.False(before.CanRetry);

        await StartThenFinishAsync(t, ctrl, roadmapId, lessonId);

        // Đã xong 1 lần → 1 lần, được làm lại.
        var after = Assert.IsType<LessonResponse>(
            Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmapId, lessonId, default)).Value);
        Assert.Equal(1, after.AttemptCount);
        Assert.True(after.CanRetry);

        // Đang luyện lại → 2 lần đã làm, nhưng KHÔNG được bấm làm lại lần nữa.
        Assert.IsType<OkObjectResult>(await ctrl.RetryLesson(roadmapId, lessonId, default));
        var during = Assert.IsType<LessonResponse>(
            Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmapId, lessonId, default)).Value);
        Assert.Equal(2, during.AttemptCount);
        Assert.False(during.CanRetry);
    }

    // ── (10) Báo cáo tiến độ hiện ĐỦ MỌI LẦN LÀM ───────────────────────────────────────
    // Đây là bằng chứng tiến bộ mà việc luyện lại sinh ra để tạo: join qua `lesson.session_id`
    // (1–1, chỉ giữ lần mới nhất) sẽ chỉ hiện MỘT điểm, và người học không thấy mình đã khá lên.
    [Fact]
    public async Task Report_HienDuMoiLanLamCuaMotBai()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var (roadmapId, _, lessonId) = Ids(r);

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var s1 = AddScoredSession(t, user, t0, ("Clarity", 40m));               // lần đầu: yếu
        var s2 = AddScoredSession(t, user, t0.AddDays(1), ("Clarity", 80m));    // làm lại: khá hơn

        // Bài đã làm 2 lần; `session_id` trỏ lần MỚI NHẤT (đúng như production sau khi làm lại).
        await t.Db.RoadmapLessons.Where(l => l.Id == lessonId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, LessonStatus.Done)
                .SetProperty(l => l.SessionId, s2));
        t.Db.RoadmapLessonAttempts.AddRange(
            new RoadmapLessonAttempt { LessonId = lessonId, SessionId = s1, AttemptNo = 1, CreatedAt = t0 },
            new RoadmapLessonAttempt { LessonId = lessonId, SessionId = s2, AttemptNo = 2, CreatedAt = t0.AddDays(1) });
        await t.Db.SaveChangesAsync();

        var svc = new RoadmapReportService(
            t.NewContext(), new Mock<IAiServiceRoadmapGenerator>().Object,
            TestDb.Thresholds(t.Db), NullLogger<RoadmapReportService>.Instance);
        var report = await svc.GetReportAsync(user, roadmapId);

        Assert.NotNull(report);
        // ĐỦ 2 điểm trên đường xu hướng, theo thứ tự thời gian, và nó CHO THẤY tiến bộ.
        Assert.Equal(2, report!.Progress.Count);
        Assert.Equal(40m, report.Progress[0].OverallPercentage);
        Assert.Equal(80m, report.Progress[1].OverallPercentage);
        Assert.All(report.Progress, p => Assert.Equal("Lesson 1", p.LessonTitle));
    }

    // ── (11) HAI request đồng thời → chỉ MỘT thắng ────────────────────────────────────
    //
    // Điều kiện trạng thái trong câu ExecuteUpdate là thứ DUY NHẤT chặn việc này, và nó CHỈ với tới
    // được dưới đua: guard `if (lesson.Status == ...)` ở đầu hàm đọc trạng thái CŨ, nên hai request
    // song song đều qua được nó. Bỏ điều kiện đó đi thì cả hai cùng link ⇒ hai buổi cùng mở cho một
    // bài, cả hai đều đã TRỪ CREDIT, và `session_id` bị kẻ về sau ghi đè ⇒ buổi kia thành mồ côi.
    //
    // Dàn dựng đua bằng callback của reserve: nó chạy ĐÚNG khe giữa lúc đọc trạng thái và lúc lật —
    // mô phỏng request kia vừa thắng.
    private static void FlipLessonToPracticing(TestDb t, Guid lessonId)
    {
        using var cmd = t.Connection.CreateCommand();
        cmd.CommandText = "UPDATE roadmap_lessons SET status = 'Practicing' WHERE id = $id;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$id";
        p.Value = lessonId.ToString().ToUpperInvariant();   // EF lưu Guid dạng TEXT hoa trên SQLite
        cmd.Parameters.Add(p);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Fact]
    public async Task Retry_DuaVoiRequestKhac_ChiMotThang_KeThuaKhongLink()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, user));

        var reservation = ReserveOk();
        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), reservation), user);
        var firstSession = await StartThenFinishAsync(t, ctrl, roadmapId, lessonId);

        // Request "kia" thắng ngay trong khe giữa đọc-trạng-thái và lật-trạng-thái.
        reservation.Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => FlipLessonToPracticing(t, lessonId))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var conflict = Assert.IsType<ConflictObjectResult>(
            await ctrl.RetryLesson(roadmapId, lessonId, default));
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        var db = t.NewContext();
        // Kẻ thua KHÔNG được ghi đè session_id và KHÔNG được ghi thêm dòng lịch sử.
        var lesson = await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lessonId);
        Assert.Equal(firstSession, lesson.SessionId);
        Assert.Equal(1, await db.RoadmapLessonAttempts.AsNoTracking().CountAsync(a => a.LessonId == lessonId));
    }

    [Fact]
    public async Task Start_DuaVoiRequestKhac_ChiMotThang_KeThuaKhongLink()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var (roadmapId, _, lessonId) = Ids(SeedRoadmap(t, user));

        var reservation = ReserveOk();
        reservation.Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => FlipLessonToPracticing(t, lessonId))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), reservation), user);

        var conflict = Assert.IsType<ConflictObjectResult>(
            await ctrl.StartLesson(roadmapId, lessonId, default));
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        var db = t.NewContext();
        var lesson = await db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lessonId);
        Assert.Null(lesson.SessionId);       // kẻ thua không link được
        Assert.Equal(0, await db.RoadmapLessonAttempts.AsNoTracking().CountAsync());
    }

    // ── (12) Màn CHI TIẾT LỘ TRÌNH cũng phải trả đúng số lần làm + cờ làm lại ─────────
    //
    // Đây là màn FE render danh sách bài + nút "Làm lại". Nó đi qua `RoadmapService.Map`, một đường
    // KHÁC hẳn `OpenLessonAsync`. Hai đường lệch nhau nghĩa là nút hiện ở màn này mà không hiện ở
    // màn kia — đúng lý do luật `canRetry` được gom về một hàm dùng chung.
    [Fact]
    public async Task ChiTietLoTrinh_TraSoLanLam_VaCoCanRetry()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var r = SeedRoadmap(t, user);
        var (roadmapId, milestoneId, doneLessonId) = Ids(r);

        // Bài thứ hai CHƯA làm lần nào — để phân biệt "0 lần" với "đếm nhầm sang bài khác".
        var freshLessonId = Guid.NewGuid();
        t.Db.RoadmapLessons.Add(new RoadmapLesson
        {
            Id = freshLessonId, MilestoneId = milestoneId, OrderNo = 2,
            Title = "Lesson 2", Status = LessonStatus.Theory
        });
        await t.Db.SaveChangesAsync();

        var ctrl = Controller(t, RealPractice(t, QuestionGenOk(), ReserveOk()), user);
        await StartThenFinishAsync(t, ctrl, roadmapId, doneLessonId);
        Assert.IsType<OkObjectResult>(await ctrl.RetryLesson(roadmapId, doneLessonId, default));
        await t.Db.RoadmapLessons.Where(l => l.Id == doneLessonId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LessonStatus.Done));

        var svc = new RoadmapService(
            t.NewContext(), new Mock<IStorageService>().Object,
            new Mock<IAiServiceRoadmapGenerator>().Object, NullLogger<RoadmapService>.Instance);
        var detail = await svc.GetAsync(user, roadmapId);

        Assert.NotNull(detail);
        var lessons = detail!.Milestones.Single().Lessons.ToDictionary(l => l.Id);
        Assert.Equal(2, lessons[doneLessonId].AttemptCount);
        Assert.True(lessons[doneLessonId].CanRetry);
        Assert.Equal(0, lessons[freshLessonId].AttemptCount);
        Assert.False(lessons[freshLessonId].CanRetry);
    }

    // Buổi B2C đã chấm + breakdown 1 tiêu chí, ghim mốc thời gian chấm (khoá sắp xếp của báo cáo).
    private static Guid AddScoredSession(
        TestDb t, Guid cand, DateTime at, params (string name, decimal pct)[] scores)
    {
        var session = TestDb.Session(cand, SessionStatus.Scored, createdAt: at);
        t.Db.PracticeSessions.Add(session);

        foreach (var (name, pct) in scores)
        {
            var criterion = t.Db.RubricCriteria.Local.FirstOrDefault(c => c.Name == name)
                ?? t.Db.RubricCriteria.FirstOrDefault(c => c.Name == name);
            if (criterion is null)
            {
                criterion = TestDb.Criterion(JobCategory.BE, name: name);
                t.Db.RubricCriteria.Add(criterion);
            }
            t.Db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                CriterionId = criterion.Id,
                CriterionName = name,
                AverageScore = Math.Round(pct / 20m, 2),
                MaxScore = 5,
                Percentage = pct,
                Weight = 1m,
                NeedsImprovement = pct < 50m,
                CreatedAt = at
            });
        }
        t.Db.SaveChanges();
        return session.Id;
    }
}
