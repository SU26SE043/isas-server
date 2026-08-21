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
    // BE-5 — thêm `Evidence`: mutation-check anchor cho "quên forward bằng chứng xuống generator".
    private sealed record GenArgs(
        IReadOnlyList<RoadmapWeakness>? Weaknesses,
        string? CvText,
        string? Focus,
        string? CvAnalysisSummary,
        string? PriorRoadmapSummary,
        IReadOnlyList<QuestionTargetCriterionDto>? Criteria,
        string Scope,
        IReadOnlyList<CriterionEvidence>? Evidence);

    private static Mock<IAiServiceRoadmapGenerator> GenMock(
        RoadmapGenAiResult result, Action<GenArgs>? capture = null)
    {
        var m = new Mock<IAiServiceRoadmapGenerator>();
        var setup = m.Setup(x => x.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapWeakness>?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<CancellationToken>()));
        if (capture is not null)
            setup.Callback<string, string, IReadOnlyList<RoadmapWeakness>?, string?, string?, string?, string?, IReadOnlyList<QuestionTargetCriterionDto>?, string, IReadOnlyList<CriterionEvidence>?, CancellationToken>(
                    (_, _, w, cv, f, ca, pr, crit, scope, evidence, _) => capture(new GenArgs(w, cv, f, ca, pr, crit, scope, evidence)))
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

    // Seed 1 buổi B2C đã Scored + N tiêu chí có điểm (BC9) → nguồn baseline/weakness.
    private static Guid SeedScoredSession(
        TestDb t, Guid candidateId, params (string name, decimal pct, bool needsImprovement)[] criteria)
    {
        var session = TestDb.Session(candidateId, SessionStatus.Scored, JobCategory.BE);
        t.Db.PracticeSessions.Add(session);
        foreach (var (name, pct, needs) in criteria)
        {
            // DÙNG LẠI tiêu chí cùng tên nếu buổi trước đã seed — production chỉ có MỘT bộ tiêu chí
            // cho mỗi (nghề, ngôn ngữ) và mọi buổi trỏ vào chính nó (unique
            // `ux_rubric_criteria_b2c_default_version_name` khoá điều đó ở tầng DB).
            var crit = t.Db.RubricCriteria.Local
                    .FirstOrDefault(c => c.Name == name && c.CandidateId == null
                                         && c.JobCategory == JobCategory.BE)
                ?? t.Db.RubricCriteria
                    .FirstOrDefault(c => c.Name == name && c.CandidateId == null
                                         && c.JobCategory == JobCategory.BE);
            if (crit is null)
            {
                crit = TestDb.Criterion(JobCategory.BE, name: name);
                t.Db.RubricCriteria.Add(crit);
            }
            t.Db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                CriterionId = crit.Id,
                CriterionName = name,
                AverageScore = 2m,
                MaxScore = 5,
                Percentage = pct,
                Weight = 1m,
                NeedsImprovement = needs,
                CreatedAt = DateTime.UtcNow
            });
        }
        t.Db.SaveChanges();
        return session.Id;
    }

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
                RoadmapStatus: nameof(Enums.RoadmapStatus.Active));
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
    [Fact]
    public async Task Post_Returns201_AndPersistsThreeTables()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null), default);

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
        Assert.Null(roadmapRow.Baseline);
        Assert.Null(roadmapRow.SourceSessionIds);

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
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Null vẫn giữ nghĩa "không gửi" → mặc định "vi" — đối chứng dương cho test rỗng ở trên, để
    // phân biệt hai giá trị đó thật sự tách bạch nhau chứ không phải cả hai cùng vô hiệu guard.
    [Fact]
    public async Task Create_NullLanguage_DefaultsToVi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, Language: null), default);

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

    // ── (2b) không chọn buổi nào + không có buổi Scored → baseline/sources null, roadmap CHUẨN ──────
    [Fact]
    public async Task Post_NoScoredSessions_BaselineNull()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.FE, RoadmapLevel.Fresher, null), default);
        Assert.IsType<CreatedResult>(result);

        var row = await t.Db.Roadmaps.AsNoTracking().SingleAsync();
        Assert.Null(row.Baseline);
        Assert.Null(row.SourceSessionIds);
        Assert.Null(captured!.Weaknesses);   // rỗng → AI sinh roadmap chuẩn theo level
    }

    // ── (2c) BC17 — ĐẢO TIỀN ĐỀ CŨ (cố ý): trước đây tạo roadmap tự GOM MỌI buổi Scored làm baseline.
    // Nay không chọn buổi nào (SessionIds null) → roadmap CHUẨN theo level: buổi Scored đang có VẪN bị
    // BỎ QUA (baseline/sources/weakness null), KHÔNG auto-gather. Đây là thay đổi hành vi có chủ đích của BC17.
    [Fact]
    public async Task Post_EmptySelection_IgnoresExistingScoredSessions_StandardRoadmap()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        // Có buổi Scored trong DB, nhưng KHÔNG được chọn.
        SeedScoredSession(t, user, ("Clarity", 40m, true), ("Depth", 80m, false));

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        // SessionIds null (mặc định) → KHÔNG query buổi nào.
        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null), default);
        Assert.IsType<CreatedResult>(result);

        var row = await t.Db.Roadmaps.AsNoTracking().SingleAsync();
        Assert.Null(row.Baseline);            // KHÔNG gom buổi Scored đang có
        Assert.Null(row.SourceSessionIds);
        Assert.NotNull(captured);
        Assert.Null(captured!.Weaknesses);    // không đẩy weakness nào xuống AI
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
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
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

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, Scope: scope), default);

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
    public async Task Post_ResolvedFrom_NoSessionsSelected_EmptySessionIds_BaselineUnavailable()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.FE, RoadmapLevel.Fresher, null), default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<RoadmapResponse>(created.Value);

        Assert.Empty(body.ResolvedFrom.SessionIds);
        Assert.False(body.ResolvedFrom.BaselineAvailable);
    }

    [Fact]
    public async Task Post_ResolvedFrom_EchoesScope_Quick()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, Scope: "Quick"), default);

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

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null), default);

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

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null), default);
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

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null), default);
        Assert.IsType<CreatedResult>(result);

        Assert.NotNull(captured);
        Assert.Empty(captured!.Criteria!);
    }

    // BE-5 — thêm 1 AnswerScore (attempt chuẩn) vào buổi `sessionId`, gắn tiêu chí `criterionName`.
    // Reasoning trích NGUYÊN VĂN (mô phỏng E11) — nguồn cho RoadmapEvidenceLoader.
    private static void SeedAnswerScore(
        TestDb t, Guid sessionId, string criterionName, decimal score, string reasoning)
    {
        var question = TestDb.Question(sessionId, order: 99);
        t.Db.PracticeQuestions.Add(question);
        var answer = TestDb.Answer(sessionId, question.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.PracticeAnswers.Add(answer);
        var crit = t.Db.RubricCriteria.Local.FirstOrDefault(c => c.Name == criterionName && c.CandidateId == null)
            ?? t.Db.RubricCriteria.FirstOrDefault(c => c.Name == criterionName && c.CandidateId == null)
            ?? TestDb.Criterion(JobCategory.BE, name: criterionName);
        if (t.Db.Entry(crit).State == EntityState.Detached) t.Db.RubricCriteria.Add(crit);
        t.Db.AnswerScores.Add(new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = crit.Id,
            AttemptNo = 1,
            Score = score,
            Reasoning = reasoning,
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        });
        t.Db.SaveChanges();
    }

    // BE-5 — anchor mutation-check: bằng chứng HÀNH VI (Reasoning thật, không phải placeholder null)
    // của tiêu chí yếu nhất phải tới ĐƯỢC generator qua CreateAsync → RoadmapEvidenceLoader.
    [Fact]
    public async Task Post_WeakCriterionHasReasoning_EvidenceReachesGenerator()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var sid = SeedScoredSession(t, user, ("Clarity", 30m, true), ("Depth", 90m, false));
        SeedAnswerScore(t, sid, "Clarity",
            score: 1m,
            reasoning: "Câu trả lời không cân nhắc đánh đổi khi ưu tiên tính năng với nguồn lực hạn chế.");

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null, SessionIds: [sid]), default);
        Assert.IsType<CreatedResult>(result);

        Assert.NotNull(captured);
        var ev = Assert.Single(captured!.Evidence!);
        Assert.Equal("Clarity", ev.CriterionName);
        Assert.Contains("đánh đổi khi ưu tiên tính năng với nguồn lực hạn chế", Assert.Single(ev.Reasoning));
        // "Depth" đạt (needsImprovement=false) → KHÔNG nằm trong danh sách weakness → KHÔNG kéo bằng chứng
        Assert.DoesNotContain(captured.Evidence!, e => e.CriterionName == "Depth");
    }

    // BE-5 — không có buổi Scored nào được chọn (baseline null) → evidence rỗng, KHÔNG lỗi.
    [Fact]
    public async Task Post_NoScoredSession_SendsEmptyEvidence_NoError()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Middle, null), default);
        Assert.IsType<CreatedResult>(result);

        Assert.NotNull(captured);
        Assert.Empty(captured!.Evidence!);
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
        var created = Assert.IsType<CreatedResult>(
            await ownerCtrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null), default));
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
        var created = Assert.IsType<CreatedResult>(
            await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null), default));
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

    // ── (4) cvId không phải của mình → 403; không tồn tại → 404 ─────────────────────
    [Fact]
    public async Task Post_CvOwnedByOther_Returns403_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, Guid.NewGuid(), "CV nội dung"));   // chủ khác

        var ctrl = Controller(t, storage.Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, cvId), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
    }

    [Fact]
    public async Task Post_CvNotFound_Returns404_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileRecord?)null);

        var ctrl = Controller(t, storage.Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, Guid.NewGuid()), default);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
    }

    // ── (5) generator throw → 502, KHÔNG có row roadmap (rollback) ──────────────────
    [Fact]
    public async Task Post_GeneratorFails_Returns502_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(x => x.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapWeakness>?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CriterionEvidence>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /generate-roadmap trả 500"));

        var ctrl = Controller(t, new Mock<IStorageService>().Object, gen.Object, user);

        var result = await ctrl.Create(new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
        Assert.False(await t.Db.RoadmapMilestones.AnyAsync());
        Assert.False(await t.Db.RoadmapLessons.AnyAsync());
    }

    // ══ BC17 — CV analysis làm bối cảnh (BC7) ════════════════════════════════════════════
    // RoadmapService KHÔNG có phụ thuộc ICreditReservationClient (tạo roadmap free — D22) ⇒ "không
    // reserve/consume credit" được bảo đảm BẰNG CẤU TRÚC. Ở đây khẳng định thêm: chỉ ĐỌC row cv_analyses
    // (bối cảnh tới được AI), KHÔNG gọi /analyze-cv (không có analyzer trong service này).

    [Fact]
    public async Task Post_CvAnalysisOwned_SummaryReachesGenerator()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var caId = SeedCvAnalysis(t, user);

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, CvAnalysisId: caId), default);
        Assert.IsType<CreatedResult>(result);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.CvAnalysisSummary);
        Assert.Contains("Ứng viên 3 năm kinh nghiệm backend.", captured.CvAnalysisSummary!);
        Assert.Contains("75", captured.CvAnalysisSummary!);   // mức khớp JD

        // KHÔNG lưu cvAnalysisId vào roadmap (không có cột → tránh migration).
        var row = await t.Db.Roadmaps.AsNoTracking().SingleAsync();
        Assert.Null(row.SourceSessionIds);   // cv-analysis KHÔNG phải baseline
    }

    [Fact]
    public async Task Post_CvAnalysisOtherOwner_Returns403_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var caId = SeedCvAnalysis(t, Guid.NewGuid());   // chủ khác

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, CvAnalysisId: caId), default);

        var o = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, o.StatusCode);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
    }

    [Fact]
    public async Task Post_CvAnalysisMissing_Returns404_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, CvAnalysisId: Guid.NewGuid()), default);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.False(await t.Db.Roadmaps.AnyAsync());
    }

    // ══ BC17 — roadmap trước làm bối cảnh (final_report, BC15) ════════════════════════════
    [Fact]
    public async Task Post_PriorRoadmapCompleted_SummaryReachesGenerator()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var priorId = SeedPriorRoadmap(t, user, completed: true);

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, PriorRoadmapId: priorId), default);
        Assert.IsType<CreatedResult>(result);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.PriorRoadmapSummary);
        Assert.Contains("Tiến bộ rõ rệt qua các buổi.", captured.PriorRoadmapSummary!);   // overallComment
        Assert.Contains("Giao tiếp tốt", captured.PriorRoadmapSummary!);                  // strengths
    }

    [Fact]
    public async Task Post_PriorRoadmapNotCompleted_Returns400_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var priorId = SeedPriorRoadmap(t, user, completed: false);   // Active, final_report null

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, PriorRoadmapId: priorId), default);

        // Chỉ có roadmap được chọn (Active) trong DB — roadmap MỚI không được tạo (400 trước persist).
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(1, await t.Db.Roadmaps.CountAsync());
        Assert.Equal(priorId, (await t.Db.Roadmaps.AsNoTracking().SingleAsync()).Id);
    }

    [Fact]
    public async Task Post_PriorRoadmapOtherOwner_Returns403_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var priorId = SeedPriorRoadmap(t, Guid.NewGuid(), completed: true);   // chủ khác

        var ctrl = Controller(t, new Mock<IStorageService>().Object, GenMock(SampleRoadmap()).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, PriorRoadmapId: priorId), default);

        var o = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, o.StatusCode);
        Assert.Equal(1, await t.Db.Roadmaps.CountAsync());   // chỉ roadmap của người khác, không tạo mới
    }

    // ══ BC17 — focus free-text ═══════════════════════════════════════════════════════════
    [Fact]
    public async Task Post_Focus_PassedToGenerator()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();

        GenArgs? captured = null;
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            GenMock(SampleRoadmap(), a => captured = a).Object, user);

        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null,
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
