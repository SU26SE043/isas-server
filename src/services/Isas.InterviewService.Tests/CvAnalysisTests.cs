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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

// BC7 — POST/GET cv-analysis (mock AIService + storage). Test qua controller để chốt status code.
public class CvAnalysisTests
{
    private static FileRecord OwnedFile(Guid fileId, Guid ownerId, string type, string? parsed)
        => new()
        {
            Id = fileId,
            UserId = ownerId,
            FileType = type,
            OriginalName = $"{type}.pdf",
            StoragePath = $"{type}/{fileId}.pdf",
            StorageBucket = "isas-files",
            MimeType = "application/pdf",
            FileSize = 1024,
            ParsedText = parsed,
            ParseStatus = "done",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static CvAnalysisAiResult SampleAi(bool withJdMatch)
        => new(
            Summary: "Ứng viên backend 3 năm C#/SQL.",
            Strengths: ["C#", "Kiến trúc microservice"],
            Weaknesses: ["Ít kinh nghiệm frontend"],
            Suggestions: ["Học thêm React"],
            JdMatch: withJdMatch ? new CvJdMatch(78, ["C#", "SQL"], ["Kubernetes"]) : null);

    private static CvAnalysisController Controller(
        TestDb t, IStorageService storage, IAiServiceCvAnalyzer ai, Guid userId,
        ICreditReservationClient? credits = null, int cvAnalysisCredits = 1)
    {
        // BC7b — config Billing:CvAnalysisCredits (mặc định 1 = tính phí); credits mock mặc định = reserve OK.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:CvAnalysisCredits"] = cvAnalysisCredits.ToString()
        }).Build();
        var service = new CvAnalysisService(
            t.Db, storage, ai, credits ?? CreditsMock().Object, config,
            NullLogger<CvAnalysisService>.Instance);
        var controller = new CvAnalysisController(service, NullLogger<CvAnalysisController>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static Mock<IAiServiceCvAnalyzer> AiMock(CvAnalysisAiResult result)
    {
        var m = new Mock<IAiServiceCvAnalyzer>();
        m.Setup(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return m;
    }

    // BC7b — reservation client mặc định: reserve trả OK, consume/release no-op (Task.CompletedTask mặc định).
    private static Mock<ICreditReservationClient> CreditsMock()
    {
        var m = new Mock<ICreditReservationClient>();
        m.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return m;
    }

    // ── (a) POST → 201 + 1 row cv_analyses (không JD) ─────────────────────────────
    [Fact]
    public async Task Post_WithoutJd_Returns201_AndPersistsRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Nội dung CV..."));

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(withJdMatch: false)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var created = Assert.IsType<CreatedResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var body = Assert.IsType<CvAnalysisResponse>(created.Value);
        Assert.Equal(cvId, body.CvId);
        Assert.Null(body.JdId);
        Assert.Equal("BE", body.JobCategory);
        Assert.Null(body.JdMatch);                       // không JD → không jdMatch
        Assert.Contains("C#", body.Strengths);

        var row = await t.Db.CvAnalyses.AsNoTracking().SingleAsync();
        Assert.Equal(user, row.CandidateId);
        Assert.Equal(cvId, row.CvId);
        Assert.Null(row.JdId);
        Assert.Equal(JobCategory.BE, row.JobCategory);
        Assert.Equal(2, row.Strengths.Count);            // jsonb round-trip
        Assert.Single(row.Weaknesses);
        Assert.Null(row.JdMatch);
    }

    // POST có JD → jdMatch được lưu + trả về.
    [Fact]
    public async Task Post_WithJd_PersistsJdMatch()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var jdId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));
        storage.Setup(s => s.GetMetadata(jdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(jdId, user, "jd", "JD..."));

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(withJdMatch: true)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, jdId, JobCategory.BE), default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<CvAnalysisResponse>(created.Value);
        Assert.Equal(jdId, body.JdId);
        Assert.NotNull(body.JdMatch);
        Assert.Equal(78, body.JdMatch!.Score);
        Assert.Contains("Kubernetes", body.JdMatch.MissingSkills);

        var row = await t.Db.CvAnalyses.AsNoTracking().SingleAsync();
        Assert.NotNull(row.JdMatch);
        Assert.Equal(78, row.JdMatch!.Score);            // jsonb value-object round-trip
    }

    // ── (b) GET của chủ → đọc đúng ────────────────────────────────────────────────
    [Fact]
    public async Task Get_Owner_ReturnsAnalysis()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);
        var created = Assert.IsType<CreatedResult>(
            await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.FE), default));
        var id = ((CvAnalysisResponse)created.Value!).Id;

        var getResult = await ctrl.Get(id, default);

        var ok = Assert.IsType<OkObjectResult>(getResult);
        var body = Assert.IsType<CvAnalysisResponse>(ok.Value);
        Assert.Equal(id, body.Id);
        Assert.Equal("FE", body.JobCategory);
    }

    // ── (c) GET của người khác → 403 ──────────────────────────────────────────────
    [Fact]
    public async Task Get_OtherUser_Returns403()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, owner, "cv", "CV..."));

        // owner tạo phân tích
        var ownerCtrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, owner);
        var created = Assert.IsType<CreatedResult>(
            await ownerCtrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default));
        var id = ((CvAnalysisResponse)created.Value!).Id;

        // stranger đọc → 403
        var strangerCtrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, stranger);
        var getResult = await strangerCtrl.Get(id, default);

        var obj = Assert.IsType<ObjectResult>(getResult);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // GET id không tồn tại → 404.
    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        using var t = new TestDb();
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            AiMock(SampleAi(false)).Object, Guid.NewGuid());

        var getResult = await ctrl.Get(Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(getResult);
    }

    // ── (d) AIService lỗi → 502 + KHÔNG tạo row ───────────────────────────────────
    [Fact]
    public async Task Post_AiFails_Returns502_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /analyze-cv trả 500"));

        var ctrl = Controller(t, storage.Object, ai.Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());   // không lưu khi AI lỗi
    }

    // POST cvId không tồn tại → 404.
    [Fact]
    public async Task Post_CvNotFound_Returns404()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileRecord?)null);

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(Guid.NewGuid(), null, JobCategory.BE), default);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());
    }

    // POST CV của người khác → 403.
    [Fact]
    public async Task Post_CvOwnedByOther_Returns403()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, Guid.NewGuid(), "cv", "CV..."));   // chủ khác

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // POST CV không parse được (parsed_text rỗng) → 400.
    [Fact]
    public async Task Post_CvUnreadable_Returns400()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "   "));   // rỗng

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());
    }

    // GET list → chỉ phân tích của chính user (mới nhất trước).
    [Fact]
    public async Task List_ReturnsOnlyOwnAnalyses()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var userCtrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);
        await userCtrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);
        await userCtrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var listResult = await userCtrl.List(default);
        var ok = Assert.IsType<OkObjectResult>(listResult);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CvAnalysisResponse>>(ok.Value);
        Assert.Equal(2, items.Count);

        // user khác → rỗng
        var otherCtrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, other);
        var otherList = Assert.IsType<OkObjectResult>(await otherCtrl.List(default));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CvAnalysisResponse>>(otherList.Value));
    }

    // ── BC7b: CV analysis TÍNH PHÍ (BC-4/D22) — reserve/consume/release ────────────

    // Có credit → reserve (owner=User, khoá=Id row) TRƯỚC gọi AI, consume SAU khi lưu row; không release.
    [Fact]
    public async Task Post_WithCredit_ReservesBeforeAi_ConsumesAfter()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        // Ghi thứ tự gọi để chốt reserve TRƯỚC AI, consume SAU.
        var calls = new List<string>();
        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("ai"))
            .ReturnsAsync(SampleAi(withJdMatch: false));

        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("reserve"))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        credits.Setup(x => x.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("consume"))
            .Returns(Task.CompletedTask);

        var ctrl = Controller(t, storage.Object, ai.Object, user, credits.Object);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        Assert.IsType<CreatedResult>(result);
        var row = await t.Db.CvAnalyses.AsNoTracking().SingleAsync();

        // reserve: owner=User, ownerId=candidate, khoá=Id row cv_analyses (operationId); consume cùng khoá.
        credits.Verify(x => x.ReserveAsync("User", user, row.Id, It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(x => x.ConsumeAsync(row.Id, It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(x => x.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(new[] { "reserve", "ai", "consume" }, calls);   // reserve TRƯỚC AI, consume SAU
    }

    // Ví hết (reserve ném 402/Insufficient) → 402 + KHÔNG row + AI KHÔNG gọi + KHÔNG consume (PAY-5).
    [Fact]
    public async Task Post_InsufficientCredit_Returns402_NoRow_NoAiCall()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var ai = AiMock(SampleAi(false));

        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientCreditException("Ví không đủ credit để phân tích CV"));

        var ctrl = Controller(t, storage.Object, ai.Object, user, credits.Object);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status402PaymentRequired, obj.StatusCode);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());   // KHÔNG lưu khi ví hết
        ai.Verify(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        credits.Verify(x => x.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // AIService lỗi (502) SAU reserve → release chỗ giữ + KHÔNG row + KHÔNG consume.
    [Fact]
    public async Task Post_AiFailsAfterReserve_ReleasesCredit_NoRow_NoConsume()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /analyze-cv trả 500"));

        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var ctrl = Controller(t, storage.Object, ai.Object, user, credits.Object);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());   // KHÔNG lưu khi AI lỗi
        credits.Verify(x => x.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(x => x.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Kill-switch Billing:CvAnalysisCredits=0 → miễn phí: KHÔNG chạm Payment nhưng vẫn lưu row.
    [Fact]
    public async Task Post_BillingDisabled_SkipsCredit_StillPersists()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);   // gọi bất kỳ → fail

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user,
            credits.Object, cvAnalysisCredits: 0);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        Assert.IsType<CreatedResult>(result);
        Assert.True(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());
        credits.VerifyNoOtherCalls();   // tính phí tắt → không đụng Payment
    }
}
