using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// `CvAnalysis.CurrentLevel` được ghi thật lúc phân tích CV nhưng KHÔNG map vào bất kỳ DTO nào —
/// wizard tạo roadmap không có cách nào đọc để điền mặc định cho bước "Trình độ hiện tại". File
/// này khoá cả hai đường đọc (chi tiết `Map` + danh sách `MapList`) và ca `null` hợp lệ.
/// </summary>
public class CvAnalysisCurrentLevelResponseTests
{
    private static FileRecord OwnedFile(Guid fileId, Guid ownerId)
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
            ParsedText = "Nội dung CV...",
            ParseStatus = "done",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static Mock<IAiServiceCvAnalyzer> AiMock(string? currentLevel)
    {
        var m = new Mock<IAiServiceCvAnalyzer>();
        m.Setup(x => x.AnalyzeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CvAnalysisAiResult(
                "Tóm tắt", ["C#"], [], [], null, CurrentLevel: currentLevel));
        return m;
    }

    private static Mock<ICreditReservationClient> CreditsMock()
    {
        var m = new Mock<ICreditReservationClient>();
        m.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return m;
    }

    private static CvAnalysisController Controller(
        TestDb t, IStorageService storage, IAiServiceCvAnalyzer ai, Guid userId)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:CvAnalysisCredits"] = "1"
        }).Build();
        var service = new CvAnalysisService(
            t.Db, storage, ai, CreditsMock().Object, config, NullLogger<CvAnalysisService>.Instance);
        var controller = new CvAnalysisController(service, NullLogger<CvAnalysisController>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    // ── (1) Chi tiết (POST + GET) — CurrentLevel lộ ra body ─────────────────────────────
    [Fact]
    public async Task Post_TraCurrentLevel_TrongResponse()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user));

        var ctrl = Controller(t, storage.Object, AiMock("Middle").Object, user);

        var created = Assert.IsType<CreatedResult>(
            await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default));
        var body = Assert.IsType<CvAnalysisResponse>(created.Value);

        Assert.Equal("Middle", body.CurrentLevel);
    }

    [Fact]
    public async Task Get_TraCurrentLevel_TrongResponse()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user));

        var ctrl = Controller(t, storage.Object, AiMock("Senior").Object, user);
        var created = Assert.IsType<CreatedResult>(
            await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default));
        var analysisId = Assert.IsType<CvAnalysisResponse>(created.Value).Id;

        var ok = Assert.IsType<OkObjectResult>(await ctrl.Get(analysisId, default));
        var body = Assert.IsType<CvAnalysisResponse>(ok.Value);

        Assert.Equal("Senior", body.CurrentLevel);
    }

    // ── (2) Danh sách — CurrentLevel lộ ra qua MapList ──────────────────────────────────
    [Fact]
    public async Task List_TraCurrentLevel_TrongResponse()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user));

        var ctrl = Controller(t, storage.Object, AiMock("Junior").Object, user);
        Assert.IsType<CreatedResult>(
            await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default));

        var ok = Assert.IsType<OkObjectResult>(await ctrl.List(default));
        var items = Assert.IsAssignableFrom<IReadOnlyList<CvAnalysisListResponse>>(ok.Value);
        var item = Assert.Single(items);

        Assert.Equal("Junior", item.CurrentLevel);
    }

    // ── (3) null là giá trị HỢP LỆ, không phải thiếu dữ liệu — cả hai đường đọc ─────────
    [Fact]
    public async Task Post_CurrentLevelNull_VanHopLe()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user));

        var ctrl = Controller(t, storage.Object, AiMock(null).Object, user);

        var created = Assert.IsType<CreatedResult>(
            await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default));
        var body = Assert.IsType<CvAnalysisResponse>(created.Value);

        Assert.Null(body.CurrentLevel);
    }

    [Fact]
    public async Task List_CurrentLevelNull_VanHopLe()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user));

        var ctrl = Controller(t, storage.Object, AiMock(null).Object, user);
        Assert.IsType<CreatedResult>(
            await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default));

        var ok = Assert.IsType<OkObjectResult>(await ctrl.List(default));
        var items = Assert.IsAssignableFrom<IReadOnlyList<CvAnalysisListResponse>>(ok.Value);

        Assert.Null(Assert.Single(items).CurrentLevel);
    }
}
