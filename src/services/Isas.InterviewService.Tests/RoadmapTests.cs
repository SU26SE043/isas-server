using System.Security.Claims;
using System.Text.Json;
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

// BC12 — POST/GET roadmaps (mock AIService generator + storage). Test qua controller để chốt status code.
public class RoadmapTests
{
    private static FileRecord OwnedFile(Guid fileId, Guid ownerId, string? parsed)
        => new()
        {
            Id = fileId,
            UserId = ownerId,
            FileType = "cv",
            OriginalName = "cv.pdf",
            StoragePath = $"cv/{fileId}.pdf",
            StorageBucket = "isas-files",
            MimeType = "application/pdf",
            FileSize = 1024,
            ParsedText = parsed,
            ParseStatus = "done",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    // 2 milestone, mile1 có 2 lesson, mile2 có 1 lesson.
    private static RoadmapGenAiResult SampleRoadmap()
        => new(new List<GeneratedMilestone>
        {
            new("Milestone 1: Nền tảng", new List<string> { "Clarity", "Depth" },
                new List<GeneratedLesson> { new("Lesson 1.1"), new("Lesson 1.2") }),
            new("Milestone 2: Nâng cao", new List<string> { "Depth" },
                new List<GeneratedLesson> { new("Lesson 2.1") })
        });

    // BC17 — snapshot mọi tham số bối cảnh gửi xuống generator (để assert cái gì tới được AI).
    // BE-1 — thêm `Criteria`: mutation-check anchor cho "criteria rỗng thay vì tiêu chí thật".
    // BE-4 — thêm `Scope`: mutation-check anchor cho "quên forward scope xuống generator".
    //
    // 🔴 REC1-B7 — `CvAnalysisSummary`/`PriorRoadmapSummary`/`Evidence`/`CurrentLevel` đã XOÁ khỏi
    // record này — cả bốn đã GỠ HẲN khỏi chữ ký `IAiServiceRoadmapGenerator.GenerateAsync`, không
    // còn gì để snapshot. `CvText` (thô) đã gỡ TRƯỚC bước này (MIS1-B5); `CurrentLevel` là con
    // đường CUỐI CÙNG từng thay chỗ nó, nay cũng gỡ nốt — không còn khoá thay thế nào.
    private sealed record GenArgs(
        IReadOnlyList<RoadmapWeakness>? Weaknesses,
        string? Focus,
        IReadOnlyList<QuestionTargetCriterionDto>? Criteria,
        string Scope,
        // MIS1-B5 — mutation-check anchor cho "quên forward mistakes xuống generator".
        IReadOnlyList<RoadmapMistake>? Mistakes);

    private static Mock<IAiServiceRoadmapGenerator> GenMock(
        RoadmapGenAiResult result, Action<GenArgs>? capture = null)
    {
        var m = new Mock<IAiServiceRoadmapGenerator>();
        var setup = m.Setup(x => x.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(),
            It.IsAny<IReadOnlyList<RoadmapMistake>?>()));
        if (capture is not null)
            setup.Callback<string, string, IReadOnlyList<RoadmapWeakness>?, string?, IReadOnlyList<QuestionTargetCriterionDto>?, string, RoadmapMode, CancellationToken, IReadOnlyList<RoadmapMistake>?>(
                    (_, _, w, f, crit, scope, _, _, mistakes) => capture(new GenArgs(w, f, crit, scope, mistakes)))
                .ReturnsAsync(result);
        else
            setup.ReturnsAsync(result);
        return m;
    }

    private static RoadmapsController Controller(
        TestDb t, IStorageService storage, IAiServiceRoadmapGenerator gen, Guid userId)
    {
        var service = new RoadmapService(t.Db, storage, gen, NullLogger<RoadmapService>.Instance);
        var controller = new RoadmapsController(
            service, new Mock<IRoadmapLessonService>().Object,
            new Mock<IRoadmapReportService>().Object, NullLogger<RoadmapsController>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    // Seed 1 buổi B2C đã Scored + N tiêu chí có điểm (BC9) → nguồn baseline/weakness. MIS1-B6 —
    // thân hàm chuyển vào TestSeed.ScoredSessionWithAnswers (dùng chung 3 file); giữ NGUYÊN chữ ký
    // ở đây để 32 call site trong file này không phải sửa. `seedContentMistakes: true` vì mọi test
    // ở đây gọi CreateAsync (Guard 3 nay đòi ≥1 lỗi nội dung — xem TestSeed.cs).
    private static Guid SeedScoredSession(
        TestDb t, Guid candidateId, params (string name, decimal pct, bool needsImprovement)[] criteria)
        => TestSeed.ScoredSessionWithAnswers(t, candidateId, JobCategory.BE, seedContentMistakes: true, criteria);

    // BC17 — buổi B2C của `ownerId` nhưng CHƯA Scored (InProgress) → không hợp lệ làm baseline.
    private static Guid SeedUnscoredSession(TestDb t, Guid ownerId)
    {
        var session = TestDb.Session(ownerId, SessionStatus.InProgress, JobCategory.BE);
        t.Db.PracticeSessions.Add(session);
        t.Db.SaveChanges();
        return session.Id;
    }

    // BC17 — seed 1 phân tích CV (BC7) thuộc `ownerId`. Trả về id để chọn làm bối cảnh.
    private static Guid SeedCvAnalysis(TestDb t, Guid ownerId)
    {
        var ca = new CvAnalysis
        {
            Id = Guid.NewGuid(),
            CandidateId = ownerId,
            CvId = Guid.NewGuid(),
            JobCategory = JobCategory.BE,
            Summary = "Ứng viên 3 năm kinh nghiệm backend.",
            Strengths = ["C#", "SQL"],
            Weaknesses = ["Thiếu kinh nghiệm hệ phân tán"],
            Suggestions = ["Học thêm microservice"],
            JdMatch = new CvJdMatch(75, ["C#"], ["Kafka"]),
            CreatedAt = DateTime.UtcNow
        };
        t.Db.CvAnalyses.Add(ca);
        t.Db.SaveChanges();
        return ca.Id;
    }

    // BC17 — seed 1 roadmap thuộc `ownerId`. completed=true → Status Completed + final_report (JSON của
    // RoadmapReportResponse, serialize CÙNG Web-defaults như RoadmapReportService); false → Active + null.
    private static Guid SeedPriorRoadmap(TestDb t, Guid ownerId, bool completed)
    {
        string? finalReport = null;
        if (completed)
        {
            var report = new RoadmapReportResponse(
                Radar: [],
                LevelEvaluation: [],
                Strengths: ["Giao tiếp tốt"],
                Weaknesses: ["Chưa sâu thuật toán"],
                Improvements: ["Luyện thêm quy hoạch động"],
                OverallComment: "Tiến bộ rõ rệt qua các buổi.",
                RoadmapStatus: nameof(Enums.RoadmapStatus.Active),
                Progress: []);
            finalReport = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        var rm = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = ownerId,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            Status = completed ? RoadmapStatus.Completed : RoadmapStatus.Active,
            FinalReport = finalReport,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.Roadmaps.Add(rm);
        t.Db.SaveChanges();
        return rm.Id;
    }

    // ── (1) POST → 201 + rows đủ 3 bảng, status + order_no đúng ────────────────────
    // MIS1-B6 — cần 1 buổi hợp lệ (Guard 1/2/3); Baseline/SourceSessionIds đổi từ Assert.Null (ca
    // "không chọn buổi nào" nay KHÔNG còn tồn tại) sang Assert.NotNull khớp buổi vừa seed.
    [Fact]
    public async Task Post_Returns201_AndPersistsThreeTables()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid]), default);

        var created = Assert.IsType<CreatedResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var body = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal("Active", body.Status);
        Assert.Equal("BE", body.JobCategory);
        Assert.Equal("Junior", body.Level);
        Assert.Null(body.CvId);

        // milestones theo orderNo 1,2
        Assert.Equal(2, body.Milestones.Count);
        Assert.Equal(new[] { 1, 2 }, body.Milestones.Select(m => m.OrderNo).ToArray());
        var m1 = body.Milestones[0];
        Assert.Equal("Pending", m1.Status);
        Assert.Null(m1.Improvement);
        Assert.Contains("Clarity", m1.FocusCriteria);
        // lessons theo orderNo 1,2, status Theory, theoryContent null
        Assert.Equal(new[] { 1, 2 }, m1.Lessons.Select(l => l.OrderNo).ToArray());
        Assert.All(m1.Lessons, l => Assert.Equal("Theory", l.Status));
        Assert.All(m1.Lessons, l => Assert.Null(l.TheoryContent));
        Assert.All(m1.Lessons, l => Assert.Null(l.SessionId));

        // DB round-trip (AsNoTracking → đọc lại từ SQLite qua value converter)
        var roadmapRow = await t.Db.Roadmaps.AsNoTracking().SingleAsync();
        Assert.Equal(user, roadmapRow.CandidateId);
        Assert.Equal(RoadmapStatus.Active, roadmapRow.Status);
        Assert.Equal(RoadmapLevel.Junior, roadmapRow.Level);
        Assert.NotNull(roadmapRow.Baseline);
        Assert.Equal(40m, roadmapRow.Baseline!["Clarity"]);
        Assert.Equal([sid], roadmapRow.SourceSessionIds);

        Assert.Equal(2, await t.Db.RoadmapMilestones.CountAsync());
        Assert.Equal(3, await t.Db.RoadmapLessons.CountAsync());
        Assert.All(await t.Db.RoadmapMilestones.AsNoTracking().ToListAsync(),
            m => Assert.Equal(MilestoneStatus.Pending, m.Status));
        Assert.All(await t.Db.RoadmapLessons.AsNoTracking().ToListAsync(),
            l => Assert.Equal(LessonStatus.Theory, l.Status));
    }

    // BK36 — chuỗi RỖNG cho `language` là một GIÁ TRỊ SAI, KHÔNG được coi như "không gửi". Mẫu
    // PracticeServiceTests.Create_EmptyLanguage_Throws_NoReserve_NoSessionRow — `ValidateLanguage`
    // trước đây dùng `IsNullOrWhiteSpace`, gộp "không gửi" (null → "vi") với "gửi rỗng" (""), nên
    // caller gõ nhầm `language: ""` ÂM THẦM nhận roadmap tiếng Việt thay vì bị từ chối.
    [Fact]
    public async Task Create_EmptyLanguage_Throws_NoRoadmapRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var gen = GenMock(SampleRoadmap());
        var service = new RoadmapService(t.Db, new Mock<IStorageService>().Object, gen.Object,
            NullLogger<RoadmapService>.Instance);
        var req = new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, Language: "");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(user, req));

        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.Verify(g => g.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<RoadmapMode>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Null vẫn giữ nghĩa "không gửi" → mặc định "vi" — đối chứng dương cho test rỗng ở trên, để
    // phân biệt hai giá trị đó thật sự tách bạch nhau chứ không phải cả hai cùng vô hiệu guard.
    // MIS1-B6 — thêm 1 buổi hợp lệ (Guard 1/2/3) để test còn tới được chỗ đo ngôn ngữ mặc định.
    [Fact]
    public async Task Create_NullLanguage_DefaultsToVi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, Language: null, SessionIds: [sid]), default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal("vi", body.Language);
    }

    // ── (2a) BC17: chọn TẬP CON buổi → baseline/weakness CHỈ từ buổi được chọn; sources = đúng id đó ──
    // (2 buổi Scored, chỉ chọn 1 → buổi kia KHÔNG lọt baseline lẫn sourceSessionIds.)
    [Fact]
    public async Task Post_ChosenSessions_BaselineAndSourcesFromThoseOnly()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var chosenId = SeedScoredSession(t, user,
            ("Clarity", 40m, true),    // yếu
            ("Depth", 80m, false));    // mạnh
        var notChosenId = SeedScoredSession(t, user,
            ("Teamwork", 90m, false)); // buổi khác — KHÔNG được chọn

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null, SessionIds: [chosenId]), default);
        Assert.IsType<CreatedResult>(result);

        var row = await t.Db.Roadmaps.AsNoTracking().SingleAsync();
        Assert.NotNull(row.Baseline);
        Assert.Equal(40m, row.Baseline!["Clarity"]);
        Assert.Equal(80m, row.Baseline["Depth"]);
        Assert.False(row.Baseline.ContainsKey("Teamwork"));   // buổi không chọn KHÔNG vào baseline

        // sourceSessionIds = ĐÚNG buổi được chọn (không có buổi kia).
        Assert.NotNull(row.SourceSessionIds);
        var only = Assert.Single(row.SourceSessionIds!);
        Assert.Equal(chosenId, only);
        Assert.DoesNotContain(notChosenId, row.SourceSessionIds!);

        // Chỉ tiêu chí needsImprovement của buổi được chọn gửi xuống AI làm weakness.
        Assert.NotNull(captured);
        var weak = Assert.Single(captured!.Weaknesses!);
        Assert.Equal("Clarity", weak.CriterionName);
        Assert.Equal(40m, weak.Percentage);
    }

    // ── (2b) MIS1-B6 — ĐẢO TIỀN ĐỀ, LÝ DO: bài gốc (`Post_NoScoredSessions_BaselineNull`) khoá "không
    // chọn buổi nào + không có buổi Scored ⇒ 201, baseline/sources null, roadmap CHUẨN theo level".
    // Guard 1 (ROADMAP_SESSIONS_REQUIRED) nay chặn CHÍNH ca đó — không còn nhánh "roadmap chuẩn theo
    // level" khi thiếu buổi. Đổi hẳn sang đối chứng: xác nhận 400, không tạo row nào, AI không được gọi.
    [Fact]
    public async Task Post_KhongChonBuoiNao_VaKhongCoBuoiScored_400_KhongTao()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var gen = GenMock(SampleRoadmap());

        var result = await Controller(t, new Mock<IStorageService>().Object, gen.Object, user)
            .Create(new CreateRoadmapRequest(JobCategory.FE, RoadmapLevel.Fresher, null), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ROADMAP_SESSIONS_REQUIRED", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // ── (2c) MIS1-B6 — ĐẢO TIỀN ĐỀ, LÝ DO: bài gốc
    // (`Post_EmptySelection_IgnoresExistingScoredSessions_StandardRoadmap`, chính nó đã là một lần
    // đảo tiền đề của BC17) khoá "SessionIds null ⇒ 201, BỎ QUA mọi buổi Scored đang có, roadmap
    // CHUẨN theo level". Guard 1 nay chặn NGAY việc gửi `SessionIds` rỗng/null — không còn đường
    // "tạo roadmap chuẩn rồi bỏ qua buổi đã có" để mà bỏ qua. Đổi hẳn sang đối chứng: dù có buổi
    // Scored sẵn trong DB, KHÔNG chọn nó (SessionIds rỗng) vẫn bị từ chối 400 — không "âm thầm
    // thành công theo cách khác" bằng cách tự động gom buổi đang có.
    [Fact]
    public async Task Post_KhongChonBuoiNao_MacDuCoBuoiScoredSan_400_KhongTuDongGom()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        // Có buổi Scored trong DB, nhưng KHÔNG được chọn.
        SeedScoredSession(t, user, ("Clarity", 40m, true), ("Depth", 80m, false));
        var gen = GenMock(SampleRoadmap());

        // SessionIds null (mặc định) → Guard 1 chặn, KHÔNG tự động gom buổi Scored đang có.
        var result = await Controller(t, new Mock<IStorageService>().Object, gen.Object, user)
            .Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ROADMAP_SESSIONS_REQUIRED", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MIS1-B6 — Guard 1 (trần số buổi) · Guard NGÔN NGỮ · Guard 3 (không lỗi nội dung)
    // ══════════════════════════════════════════════════════════════════════════

    // Guard 1, vế trần — mã RIÊNG với vế "chưa chọn buổi nào" (ROADMAP_SESSIONS_REQUIRED).
    [Fact]
    public async Task Post_QuaTranSoBuoi_400_KhongTaoVaKhongGoiAI()
    {
        using var t = new TestDb();
        var gen = GenMock(SampleRoadmap());
        var tooMany = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToList();

        var result = await Controller(t, new Mock<IStorageService>().Object, gen.Object, Guid.NewGuid())
            .Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: tooMany), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ROADMAP_TOO_MANY_SESSIONS", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // Guard NGÔN NGỮ — buổi tiếng Anh làm nguồn cho lộ trình tiếng Việt (mặc định, không gửi
    // Language) phải bị từ chối TRƯỚC khi RoadmapMistakeLoader trích nguyên văn tiếng Anh.
    [Fact]
    public async Task Post_BuoiLechNgonNgu_400_KhongGoiAI()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var session = TestDb.Session(user, SessionStatus.Scored, JobCategory.BE, language: "en");
        t.Db.PracticeSessions.Add(session);
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        t.Db.RubricCriteria.Add(crit);
        t.Db.SessionCriterionScores.Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(), SessionId = session.Id, CriterionId = crit.Id, CriterionName = "Clarity",
            AverageScore = 2m, MaxScore = crit.MaxScore, Percentage = 40m, Weight = 1m,
            NeedsImprovement = true, CreatedAt = DateTime.UtcNow
        });
        t.Db.SaveChanges();
        var gen = GenMock(SampleRoadmap());

        // Request KHÔNG gửi Language → mặc định "vi", lệch với session "en".
        var result = await Controller(t, new Mock<IStorageService>().Object, gen.Object, user)
            .Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [session.Id]), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var message = bad.Value!.ToString()!;
        Assert.Contains("ROADMAP_LANGUAGE_MISMATCH", message);
        Assert.Contains(session.Id.ToString(), message);
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // 🔒 Vá theo báo cáo kiểm — mutation "Count > MaxSourceSessions → Count >= MaxSourceSessions"
    // (off-by-one) chạy qua XANH vì KHÔNG test nào gửi ĐÚNG 20 buổi hợp lệ. Bài này khoá biên TRÊN:
    // đúng 20 buổi (mỗi buổi có điểm yếu + lỗi nội dung riêng) phải qua được Guard 1, không bị chặn
    // nhầm là "quá nhiều".
    [Fact]
    public async Task Post_DungTranSoBuoi_20Buoi_201_KhongBiGuard1ChanNham()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sessionIds = Enumerable.Range(0, 20)
            .Select(_ => SeedScoredSession(t, user, ("Clarity", 40m, true)))
            .ToList();

        var result = await Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user)
            .Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: sessionIds), default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(1, await t.Db.Roadmaps.CountAsync());
    }

    // 🔒 Vá theo báo cáo kiểm — mutation "gỡ vế `Count: > 0` khỏi Guard 1 (chỉ còn `is null`)" chạy
    // qua XANH vì không test nào gửi `SessionIds: []` (mảng RỖNG TƯỜNG MINH — khác hẳn không gửi
    // field/null, vốn đã có test riêng ở `LevelUp_KhongCoBuoiLuyenNao_400_KhongTaoDuocNua`). Thiếu
    // vế `Count: > 0`, request `[]` rơi lọt qua Guard 1, quá cửa `is { Count: > 0 }` (SAI —
    // `req.SessionIds is { Count: > 0 }` với mảng rỗng khớp `false`... nhưng vòng `if (req.SessionIds
    // is { Count: > 0 }) { ... }` bên dưới KHÔNG chạy nên `weaknesses` vẫn null) ⇒ rơi xuống Guard 2
    // với message SAI: "ROADMAP_NO_WEAKNESS" thay vì "ROADMAP_SESSIONS_REQUIRED" — đúng mã lỗi
    // frontend cần so khớp để hiển thị đúng lời khuyên cho người dùng.
    [Fact]
    public async Task Post_SessionIdsMangRong_400_ROADMAP_SESSIONS_REQUIRED_PhanBietVoiNull()
    {
        using var t = new TestDb();
        var gen = GenMock(SampleRoadmap());

        var result = await Controller(t, new Mock<IStorageService>().Object, gen.Object, Guid.NewGuid())
            .Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: []), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ROADMAP_SESSIONS_REQUIRED", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // Guard 3 — buổi CÓ điểm yếu (Guard 2 qua) nhưng tiêu chí yếu đó chấm bằng DeliveryMetrics
    // (số đo âm học, không phải câu trả lời) ⇒ RoadmapMistakeLoader trích được 0 lỗi NỘI DUNG.
    [Fact]
    public async Task Post_KhongTrichDuocLoiNoiDung_400_ROADMAP_NO_CONTENT_MISTAKES_KhongTao()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var session = TestDb.Session(user, SessionStatus.Scored, JobCategory.BE);
        t.Db.PracticeSessions.Add(session);
        var crit = TestDb.Criterion(JobCategory.BE, name: "Sự trôi chảy");
        crit.ScoringMethod = CriterionScoringMethod.DeliveryMetrics;   // chấm bằng VAD, không phải câu trả lời
        t.Db.RubricCriteria.Add(crit);
        t.Db.SessionCriterionScores.Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(), SessionId = session.Id, CriterionId = crit.Id, CriterionName = "Sự trôi chảy",
            AverageScore = 2m, MaxScore = crit.MaxScore, Percentage = 20m, Weight = 1m,
            NeedsImprovement = true, CreatedAt = DateTime.UtcNow   // Guard 2 (weakness) QUA được
        });
        t.Db.SaveChanges();
        var gen = GenMock(SampleRoadmap());

        var result = await Controller(t, new Mock<IStorageService>().Object, gen.Object, user)
            .Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [session.Id]), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ROADMAP_NO_CONTENT_MISTAKES", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // ── BE-4 — `scope`: tập đóng, case-sensitive, "" là GIÁ TRỊ SAI (mẫu ValidateLanguage/BK36) ──
    [Fact]
    public async Task Post_InvalidScope_Throws_NoRoadmapRow_NoGeneratorCall()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var gen = GenMock(SampleRoadmap());
        var service = new RoadmapService(t.Db, new Mock<IStorageService>().Object, gen.Object,
            NullLogger<RoadmapService>.Instance);
        var req = new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, Scope: "Extreme");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(user, req));

        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.Verify(g => g.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<RoadmapMode>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // BK36 — chuỗi RỖNG là GIÁ TRỊ SAI đã gửi, KHÔNG được nuốt thành mặc định "Standard".
    [Fact]
    public async Task Post_EmptyScope_Throws_NoRoadmapRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var service = new RoadmapService(t.Db, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap()).Object, NullLogger<RoadmapService>.Instance);
        var req = new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, Scope: "");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(user, req));

        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
    }

    [Theory]
    [InlineData("Quick")]
    [InlineData("Standard")]
    public async Task Post_ValidScope_ForwardsToGenerator(string scope)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid], Scope: scope), default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(scope, captured!.Scope);
    }

    // ── BE-4 — `resolvedFrom`: provenance echo lại trong response (không chỉ ghi xuống DB) ──────
    //
    // Trước BE-4, `sourceSessionIds`/`baseline` được RoadmapService.CreateAsync ghi xuống DB nhưng
    // KHÔNG endpoint nào đọc lại — cột chết ở tầng API dù có ở tầng lưu trữ (candidate chọn report
    // trong wizard rồi sau khi tạo xong KHÔNG CÒN cách nào xem lại đã dựa trên gì).
    [Fact]
    public async Task Post_ResolvedFrom_EchoesSelectedSessionIds_AndBaselineAvailable()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var chosenId = SeedScoredSession(t, user, ("Clarity", 40m, true));
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null, SessionIds: [chosenId]),
            default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<RoadmapResponse>(created.Value);

        // 🔒 Mutation-check anchor: `resolvedFrom.sessionIds` luôn trả [] bất kể input → test này đỏ.
        var only = Assert.Single(body.ResolvedFrom.SessionIds);
        Assert.Equal(chosenId, only);
        Assert.True(body.ResolvedFrom.BaselineAvailable);
    }

    [Fact]
    public async Task Post_KhongChonBuoiNao_400_KhongConDuongBaselineUnavailable()
    {
        // 🔴 MIS1-B6 — ĐẢO TIỀN ĐỀ, LÝ DO: bài gốc
        // (`Post_ResolvedFrom_NoSessionsSelected_EmptySessionIds_BaselineUnavailable`) khoá đúng
        // hành vi "không chọn buổi nào" trả 201 với `resolvedFrom.sessionIds=[]`/`baselineAvailable
        // =false`. Guard 1 (ROADMAP_SESSIONS_REQUIRED) nay chặn CHÍNH kịch bản đó — không còn
        // đường nào để roadmap "chuẩn theo level" ra đời mà không có buổi làm nguồn. Đổi hẳn sang
        // đối chứng: xác nhận đây LÀ 400, không phải 201 rỗng nghĩa.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.FE, RoadmapLevel.Fresher, null), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ROADMAP_SESSIONS_REQUIRED", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
    }

    [Fact]
    public async Task Post_ResolvedFrom_EchoesScope_Quick()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap()).Object, user);
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid], Scope: "Quick"), default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal("Quick", body.ResolvedFrom.Scope);
    }

    [Fact]
    public async Task Post_ResolvedFrom_ScopeOmitted_DefaultsToStandardInResponse()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap()).Object, user);
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid]), default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal("Standard", body.ResolvedFrom.Scope);
    }

    // `scope` KHÔNG được lưu DB (task BE-4 cố ý không thêm cột/migration) — đọc lại roadmap CŨ qua
    // GetAsync không thể biết scope lúc tạo. Response PHẢI nói thật "không biết" (null), KHÔNG suy
    // đoán từ số milestone/lesson hiện có (mẫu BK23: null = không biết, đừng bịa "khác" từ "không
    // biết"). `sessionIds`/`baselineAvailable` VẪN echo đúng vì hai cái đó CÓ persist ở DB sẵn.
    [Fact]
    public async Task Get_ResolvedFrom_ScopeIsNull_ButSessionIdsAndBaselineStillEchoFromDb()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var chosenId = SeedScoredSession(t, user, ("Clarity", 40m, true));
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap()).Object, user);
        var created = Assert.IsType<CreatedResult>(await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null, SessionIds: [chosenId],
                Scope: "Quick"),
            default));
        var createdBody = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal("Quick", createdBody.ResolvedFrom.Scope);   // đối chứng: NGAY LÚC TẠO có giá trị

        var getResult = await ctrl.Get(createdBody.Id, default);

        var getBody = Assert.IsType<RoadmapResponse>(Assert.IsType<OkObjectResult>(getResult).Value);
        Assert.Null(getBody.ResolvedFrom.Scope);                 // đọc lại → KHÔNG biết → null
        var only = Assert.Single(getBody.ResolvedFrom.SessionIds);
        Assert.Equal(chosenId, only);
        Assert.True(getBody.ResolvedFrom.BaselineAvailable);
    }

    // ── BE-1 — CreateAsync phải gửi TIÊU CHÍ NĂNG LỰC THẬT xuống AIService, không phải rỗng ──
    //
    // Đo trên production: chỉ 7% `milestone.focusCriteria` khớp tên tiêu chí thật vì AIService
    // trước đây KHÔNG hề được cấp danh sách này. Mutation-check anchor: gỡ `LoadCriteriaNamesAsync`
    // khỏi `CreateAsync` (hoặc gửi rỗng thay vì danh sách thật) → test này ĐỎ.
    [Fact]
    public async Task Post_SendsRealCriterionNames_ForJobCategoryAndLanguage_ToGenerator()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        // Seed đúng bộ tiêu chí mặc định (candidate_id=null, campaign_id=null) cho (BE, vi) —
        // giống hệt cách production seed rubric B2C (BC11).
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, name: "Phân tích yêu cầu"));
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, name: "Giao tiếp & trình bày"));
        // Tiêu chí nghề KHÁC (FE) không được lẫn vào.
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.FE, name: "Không liên quan"));
        t.Db.SaveChanges();
        var sid = SeedScoredSession(t, user, ("Phân tích yêu cầu", 40m, true));   // MIS1-B6 — Guard 1/2/3

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null, SessionIds: [sid]), default);
        Assert.IsType<CreatedResult>(result);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Criteria);
        var names = captured.Criteria!.Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(["Giao tiếp & trình bày", "Phân tích yêu cầu"], names);
    }

    // Không có tiêu chí nào seed cho (nghề, ngôn ngữ) đó → gửi rỗng, KHÔNG lỗi (backward-compat: hệ
    // thống prod luôn có seed BC11, nhưng test này khoá lại rằng thiếu dữ liệu không làm gãy roadmap).
    [Fact]
    public async Task Post_NoCriterionSeeded_SendsEmptyCriteria_NoError()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        // MIS1-B6 — cần 1 buổi hợp lệ (Guard 1/2/3) mà KHÔNG được lộ tiêu chí (BE, vi) nào cho
        // LoadCriteriaNamesAsync — dùng thẳng SeedScoredSession sẽ VÔ TÌNH tạo đúng tiêu chí (BE, vi)
        // mà test này cố tình kiểm là RỖNG. Seed TAY 1 buổi + 1 tiêu chí NGÔN NGỮ KHÁC ("en"):
        // LoadCriteriaNamesAsync lọc theo Language=="vi" nên vẫn thấy rỗng, trong khi Guard 1/2/3
        // (chỉ đòi khớp CriterionId, không đòi khớp Language của tiêu chí) vẫn qua được.
        var session = TestDb.Session(user, SessionStatus.Scored, JobCategory.BE);
        t.Db.PracticeSessions.Add(session);
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity", language: "en");
        t.Db.RubricCriteria.Add(crit);
        t.Db.SessionCriterionScores.Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(), SessionId = session.Id, CriterionId = crit.Id, CriterionName = "Clarity",
            AverageScore = 2m, MaxScore = crit.MaxScore, Percentage = 40m, Weight = 1m,
            NeedsImprovement = true, CreatedAt = DateTime.UtcNow
        });
        var question = TestDb.Question(session.Id, order: 500);
        t.Db.PracticeQuestions.Add(question);
        var answer = TestDb.Answer(session.Id, question.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        answer.Transcript = "Câu trả lời của ứng viên cho Clarity";
        t.Db.PracticeAnswers.Add(answer);
        t.Db.AnswerScores.Add(new AnswerScore
        {
            Id = Guid.NewGuid(), AnswerId = answer.Id, CriterionId = crit.Id, AttemptNo = 1, Score = 1,
            Reasoning = "Chưa nắm vững Clarity.", RubricVersion = 1, CreatedAt = DateTime.UtcNow
        });
        t.Db.SaveChanges();

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null, SessionIds: [session.Id]), default);
        Assert.IsType<CreatedResult>(result);

        Assert.NotNull(captured);
        Assert.Empty(captured!.Criteria!);
    }

    // 🔴 REC1-B7 — test gốc `Post_WeakCriterionHasReasoning_EvidenceKhongConDuocGuiXuongGenerator`
    // (MIS1-B5: chứng minh "evidence luôn null tới generator" bằng runtime null-check) đã XOÁ —
    // `evidence` nay đã gỡ HẲN khỏi chữ ký `IAiServiceRoadmapGenerator.GenerateAsync` (không chỉ
    // khỏi payload như MIS1-B5), nên KHÔNG CÒN GÌ để null-check: `GenArgs` không có field `Evidence`
    // nữa, và bản thân trình biên dịch đã chặn mọi caller cố gửi nó — bảo đảm mạnh hơn một bài test
    // runtime. `SeedAnswerScore` (helper dựng riêng cho test đã xoá, zero caller khác trong file)
    // xoá cùng — mẫu `BuildCvAnalysisSummary`/`RoadmapService.cs` (REC1-B7): helper tự chứa, xoá
    // không kéo theo thay đổi field/DI nào khác thì xoá luôn, đừng để rác.
    //
    // 🔴 MIS1-B6 — ĐẢO TIỀN ĐỀ, LÝ DO: bài gốc (MIS1-B5, nay đã xoá — xem comment ngay trên) chứng
    // minh "evidence luôn null, KỂ CẢ KHI không chọn buổi nào" — dùng ca "không chọn buổi" làm một
    // trong hai điểm đo. Guard 1 nay chặn đúng ca đó TRƯỚC khi evidence (khi nó còn tồn tại) có cơ
    // hội được tính tới. Bài này giữ nguyên vai trò xác nhận vế còn lại: "không chọn buổi nào" là
    // 400, không phải một đường tạo-thành-công.
    [Fact]
    public async Task Post_KhongChonBuoiNao_400_KhongConDuongEvidenceLuonNull()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var gen = GenMock(SampleRoadmap());

        var result = await Controller(t, new Mock<IStorageService>().Object, gen.Object, user)
            .Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ROADMAP_SESSIONS_REQUIRED", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // ── (2d) BC17 — id buổi thiếu / khác chủ / chưa Scored → 404 batch, KHÔNG lộ id nào, KHÔNG lưu row ──
    [Theory]
    [InlineData("missing")]      // id không tồn tại
    [InlineData("other-owner")]  // buổi của người khác
    [InlineData("not-scored")]   // buổi của mình nhưng chưa Scored
    public async Task Post_ChosenSessionInvalid_Returns404_NoRow(string kind)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var okId = SeedScoredSession(t, user, ("Clarity", 40m, true));   // 1 buổi hợp lệ

        var badId = kind switch
        {
            "missing" => Guid.NewGuid(),
            "other-owner" => SeedScoredSession(t, Guid.NewGuid(), ("Clarity", 40m, true)),
            "not-scored" => SeedUnscoredSession(t, user),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [okId, badId]), default);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.False(await t.Db.Roadmaps.AnyAsync());   // AI-before-persist → không có row
    }

    // ── (3) GET owner → đầy đủ; stranger → 403; id lạ → 404 ────────────────────────
    [Fact]
    public async Task Get_Owner_Full_Stranger_403_Missing_404()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        var ownerCtrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, owner);
        var sid = SeedScoredSession(t, owner, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3
        var created = Assert.IsType<CreatedResult>(
            await ownerCtrl.Create(
                new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid]), default));
        var id = ((RoadmapResponse)created.Value!).Id;

        // owner → 200 đầy đủ
        var ok = Assert.IsType<OkObjectResult>(await ownerCtrl.Get(id, default));
        var body = Assert.IsType<RoadmapResponse>(ok.Value);
        Assert.Equal(id, body.Id);
        Assert.Equal(2, body.Milestones.Count);

        // stranger → 403
        var strangerCtrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, stranger);
        var forbidden = Assert.IsType<ObjectResult>(await strangerCtrl.Get(id, default));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        // id không tồn tại → 404
        Assert.IsType<NotFoundObjectResult>(await ownerCtrl.Get(Guid.NewGuid(), default));
    }

    // ── (3b) list chỉ của mình + KHÔNG kèm cây milestone/lesson (GET {id} thì có) ───
    // Trước đây test này khoá "list bỏ theoryContent" (list vẫn trả cả cây, chỉ rỗng phần lý thuyết).
    // Nay list KHÔNG trả cây nữa (RoadmapSummaryResponse) nên tính chất cũ được bao bởi tính chất
    // mạnh hơn: không có lesson nào trong payload thì cũng không có theoryContent nào lọt.
    [Fact]
    public async Task List_OwnOnly_OmitsMilestoneTree()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3
        var created = Assert.IsType<CreatedResult>(
            await ctrl.Create(
                new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid]), default));
        var id = ((RoadmapResponse)created.Value!).Id;

        // Giả lập BC14 đã sinh lý thuyết cho 1 lesson.
        var lesson = await t.Db.RoadmapLessons.OrderBy(l => l.OrderNo).FirstAsync();
        lesson.TheoryContent = "## Lý thuyết";
        lesson.TheoryGeneratedAt = DateTime.UtcNow;
        await t.Db.SaveChangesAsync();

        // GET {id} → có theoryContent
        var getOk = Assert.IsType<OkObjectResult>(await ctrl.Get(id, default));
        var detail = Assert.IsType<RoadmapResponse>(getOk.Value);
        Assert.Contains(detail.Milestones.SelectMany(m => m.Lessons), l => l.TheoryContent == "## Lý thuyết");

        // LIST → chỉ metadata, KHÔNG có cây milestone/lesson (⇒ cũng không có theoryContent).
        var listOk = Assert.IsType<OkObjectResult>(await ctrl.List(default));
        var items = Assert.IsAssignableFrom<IReadOnlyList<RoadmapSummaryResponse>>(listOk.Value);
        var listed = Assert.Single(items);
        Assert.Equal(id, listed.Id);
        Assert.DoesNotContain("Milestones", listed.GetType().GetProperties().Select(p => p.Name));

        // user khác → list rỗng
        var otherCtrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, other);
        var otherOk = Assert.IsType<OkObjectResult>(await otherCtrl.List(default));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<RoadmapSummaryResponse>>(otherOk.Value));
    }

    // 🔴 REC1-B7 — ĐẢO TIỀN ĐỀ: mục (4) gốc "cvId không phải của mình → 403; không tồn tại → 404"
    // không còn đúng — `ReadOwnedParsedTextAsync` (kiểm quyền rồi vứt nội dung `_ =`) đã gỡ khỏi
    // `CreateAsync`. `CvId` nay CHỈ còn lưu xuống `roadmaps.cv_id` (FK Restrict → file_records).
    //
    // `Post_CvOwnedByOther_Returns403_NoRow` (gốc) → viết lại: cùng fixture (file thật, CHỦ KHÁC)
    // nhưng seed THẲNG vào `t.Db.FileRecords` thay vì mock `IStorageService` — sau khi gỡ
    // `ReadOwnedParsedTextAsync`, service không còn gọi `IStorageService` cho `CvId` nữa, nên mock
    // đó không còn mô phỏng đúng đường đi thật (cái THẬT SỰ chặn/không-chặn `CvId` bây giờ là FK
    // của chính DB). `OwnedFile` (helper cũ) TÁI DÙNG để dựng đúng shape hàng — chỉ đổi chỗ ghi.
    [Fact]
    public async Task Post_CvOwnedByOther_VanTao201_KhongNem403()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        t.Db.FileRecords.Add(OwnedFile(cvId, Guid.NewGuid(), "CV nội dung"));   // chủ khác — file THẬT
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3
        await t.Db.SaveChangesAsync();

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, cvId, SessionIds: [sid]), default);

        Assert.IsType<CreatedResult>(result);
    }

    // `Post_CvNotFound_Returns404_NoRow` (gốc) KHÔNG viết lại theo cùng khuôn: id hoàn toàn không
    // tồn tại (không có row `file_records` nào) nay phụ thuộc hành vi FK Restrict của chính DB
    // (chặn bằng lỗi ràng buộc thay vì một câu 404 riêng) — đây là mối quan tâm KHÁC, ngoài phạm vi
    // XONG-KHI của bước này (chỉ đòi "gửi cvId ⇒ 201, bị bỏ qua", không đòi "cvId bịa cũng phải
    // qua được"). Đã nêu rõ trong RoadmapCurrentLevelTests.cs.

    // ── (5) generator throw → 502, KHÔNG có row roadmap (rollback) ──────────────────
    [Fact]
    public async Task Post_GeneratorFails_Returns502_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(x => x.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<RoadmapMode>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .ThrowsAsync(new AiServiceException("AIService /generate-roadmap trả 500"));

        var ctrl = Controller(t, new Mock<IStorageService>().Object, gen.Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid]), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
        Assert.False(await t.Db.RoadmapMilestones.AnyAsync());
        Assert.False(await t.Db.RoadmapLessons.AnyAsync());
    }

    // 🔴 REC1-B7 — ĐẢO TIỀN ĐỀ TOÀN BỘ MỤC "BC17 — CV analysis làm bối cảnh (BC7)": khối
    // `CvAnalysisId` (3 guard 404/403/400 + `BuildCvAnalysisSummary` làm ngữ cảnh prompt) đã GỠ HẲN
    // khỏi `CreateAsync`. `SeedCvAnalysis` (fixture cũ) TÁI DÙNG NGUYÊN VẸN — chính vì dữ liệu đó
    // TỪNG kích hoạt 404/403 mà nay không còn nữa mới là bằng chứng có giá trị (không phải một
    // fixture rỗng ngẫu nhiên tình cờ không trúng guard nào).
    [Fact]
    public async Task Post_CvAnalysisId_GuiGiCungDuoc_VanTao201_KhongConTacDung()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var caCuaNguoiKhac = SeedCvAnalysis(t, Guid.NewGuid());   // chủ khác — TỪNG là ca 403
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], CvAnalysisId: caCuaNguoiKhac), default);

        Assert.IsType<CreatedResult>(result);
    }

    [Fact]
    public async Task Post_CvAnalysisId_KhongTonTai_VanTao201_KhongConTacDung()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], CvAnalysisId: Guid.NewGuid()), default);   // id KHÔNG tồn tại — TỪNG là ca 404

        Assert.IsType<CreatedResult>(result);
    }

    // 🔴 REC1-B7 — cùng lý do, ĐẢO TIỀN ĐỀ TOÀN BỘ mục "BC17 — roadmap trước làm bối cảnh (BC15)":
    // khối `PriorRoadmapId` (4 guard + `BuildPriorRoadmapSummary` làm ngữ cảnh) đã GỠ HẲN.
    // `SeedPriorRoadmap` (fixture cũ) TÁI DÙNG NGUYÊN VẸN cho cùng lý do đã nêu ở CvAnalysisId.
    [Fact]
    public async Task Post_PriorRoadmapId_GuiGiCungDuoc_VanTao201_KhongConTacDung()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var priorCuaNguoiKhac = SeedPriorRoadmap(t, Guid.NewGuid(), completed: true);   // chủ khác — TỪNG là ca 403
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], PriorRoadmapId: priorCuaNguoiKhac), default);

        Assert.IsType<CreatedResult>(result);
    }

    [Fact]
    public async Task Post_PriorRoadmapId_ChuaHoanThanh_VanTao201_KhongConTacDung()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var priorChuaXong = SeedPriorRoadmap(t, user, completed: false);   // Active, final_report null — TỪNG là ca 400
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], PriorRoadmapId: priorChuaXong), default);

        Assert.IsType<CreatedResult>(result);
        // 2 roadmap trong DB: 1 cái Active seed sẵn (priorChuaXong) + 1 cái MỚI vừa tạo.
        Assert.Equal(2, await t.Db.Roadmaps.CountAsync());
    }

    [Fact]
    public async Task Post_PriorRoadmapId_KhongTonTai_VanTao201_KhongConTacDung()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                SessionIds: [sid], PriorRoadmapId: Guid.NewGuid()), default);   // id KHÔNG tồn tại — TỪNG là ca 404

        Assert.IsType<CreatedResult>(result);
    }

    // ══ BC17 — focus free-text ═══════════════════════════════════════════════════════════
    [Fact]
    public async Task Post_Focus_PassedToGenerator()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user, ("Clarity", 40m, true));   // MIS1-B6 — Guard 1/2/3

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [sid],
                Focus: "  Tập trung vào system design  "), default);
        Assert.IsType<CreatedResult>(result);

        Assert.NotNull(captured);
        Assert.Equal("Tập trung vào system design", captured!.Focus);   // đã trim
    }

    [Fact]
    public async Task Post_FocusTooLong_Returns400_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
                Focus: new string('x', 2001)), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
    }
}
