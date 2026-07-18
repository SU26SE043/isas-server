using System.Security.Claims;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// JD nhập dạng TEXT ở luồng B2C (tạo buổi luyện + phân tích CV) — áp quy ước C11 của B2B/Campaign:
/// `jdText` song song `jdId`, TEXT ƯU TIÊN FILE. Đối chiếu: CampaignTextInputTests (bên Campaign).
/// KHÔNG có cột jd_text: JD text chỉ là input sinh câu hỏi/phân tích, không ai đọc lại sau khi tạo.
/// </summary>
public class JdTextInputTests
{
    // ── Helpers dùng chung ────────────────────────────────────────────────────
    private static Mock<ICreditReservationClient> CreditsMock()
    {
        var m = new Mock<ICreditReservationClient>();
        m.Setup(x => x.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return m;
    }

    private static Mock<IAiServiceQuestionGenerator> GeneratorMock()
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });
        return gen;
    }

    private static PracticeService BuildPractice(TestDb t, IStorageService storage, IAiServiceQuestionGenerator gen)
        => new(
            t.Db, storage, gen, new Mock<ISessionScoringNotifier>().Object,
            CreditsMock().Object, NullLogger<PracticeService>.Instance);

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

    private static CvAnalysisController CvController(TestDb t, IStorageService storage, IAiServiceCvAnalyzer ai, Guid userId)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:CvAnalysisCredits"] = "1"
        }).Build();
        var service = new CvAnalysisService(
            t.Db, storage, ai, CreditsMock().Object, config, NullLogger<CvAnalysisService>.Instance);
        var controller = new CvAnalysisController(service, NullLogger<CvAnalysisController>.Instance);
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

    private static Mock<IAiServiceCvAnalyzer> CvAiMock(CvJdMatch? jdMatch)
    {
        var m = new Mock<IAiServiceCvAnalyzer>();
        m.Setup(x => x.AnalyzeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CvAnalysisAiResult(
                Summary: "Tóm tắt", Strengths: ["C#"], Weaknesses: ["FE"], Suggestions: ["Học React"],
                JdMatch: jdMatch));
        return m;
    }

    // ── (a) Tạo buổi luyện: jdText (không file) → AI nhận đúng text, KHÔNG đọc storage ─────────
    [Fact]
    public async Task Create_session_voi_jdText_gui_thang_text_cho_AI_va_khong_doc_file()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        var gen = GeneratorMock();

        var res = await BuildPractice(t, storage.Object, gen.Object).CreateSessionAsync(
            candidate,
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, JdText: "  Tuyển BE Java 3 năm  "));

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        // Text được trim rồi đưa thẳng vào prompt sinh câu hỏi.
        gen.Verify(g => g.GenerateQuestionsAsync(
            "BE", null, "Tuyển BE Java 3 năm", It.IsAny<CancellationToken>()), Times.Once);
        // Không có file nào phải parse.
        storage.Verify(s => s.GetParseTextAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        var saved = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Null(saved.JdId);
        Assert.Null(res.JdId);
    }

    // ── (b) Tạo buổi luyện: gửi CẢ jdText lẫn jdId → TEXT THẮNG, file bị bỏ hẳn (C11) ──────────
    [Fact]
    public async Task Create_session_gui_ca_text_va_file_thi_text_thang_va_khong_luu_jdId()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var jdId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        // Parser trả text KHÁC — nếu file KHÔNG bị bỏ, AI sẽ nhận "TU-FILE" thay vì text nhập tay.
        storage.Setup(s => s.GetParseTextAsync(jdId, It.IsAny<CancellationToken>())).ReturnsAsync("TU-FILE");
        var gen = GeneratorMock();

        var res = await BuildPractice(t, storage.Object, gen.Object).CreateSessionAsync(
            candidate,
            new CreatePracticeSessionRequest(null, jdId, JobCategory.BE, JdText: "JD nhập tay"));

        gen.Verify(g => g.GenerateQuestionsAsync(
            "BE", null, "JD nhập tay", It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(s => s.GetParseTextAsync(jdId, It.IsAny<CancellationToken>()), Times.Never);

        // jd_id KHÔNG được lưu: file không góp gì vào câu hỏi thì row đừng "nhận vơ" nó.
        var saved = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Null(saved.JdId);
    }

    // ── (c) jdText rỗng/khoảng trắng = KHÔNG nhập → rơi về jdId (không phá luồng file cũ) ──────
    [Fact]
    public async Task Create_session_jdText_toan_khoang_trang_thi_roi_ve_file()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var jdId = Guid.NewGuid();

        // Nhánh này THỰC SỰ lưu jd_id → phải có row files thật (SQLite CÓ enforce FK ở EF10).
        t.Db.FileRecords.Add(OwnedFile(jdId, candidate, "jd", "TU-FILE"));
        await t.Db.SaveChangesAsync();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetParseTextAsync(jdId, It.IsAny<CancellationToken>())).ReturnsAsync("TU-FILE");
        var gen = GeneratorMock();

        var res = await BuildPractice(t, storage.Object, gen.Object).CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, jdId, JobCategory.BE, JdText: "   "));

        gen.Verify(g => g.GenerateQuestionsAsync(
            "BE", null, "TU-FILE", It.IsAny<CancellationToken>()), Times.Once);

        var saved = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Equal(jdId, saved.JdId);   // luồng file cũ giữ nguyên
    }

    // ── (d) cv-analysis: jdText → AI nhận text + jdMatch KHÔNG bị vứt dù jdId null ─────────────
    [Fact]
    public async Task CvAnalysis_voi_jdText_giu_jdMatch_du_khong_co_jdId()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Nội dung CV"));
        var ai = CvAiMock(new CvJdMatch(78, ["C#"], ["K8s"]));

        var result = await CvController(t, storage.Object, ai.Object, user)
            .Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE, JdText: " JD dán tay "), default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<CvAnalysisResponse>(created.Value);
        Assert.Null(body.JdId);
        // Gate jdMatch theo "có nội dung JD", KHÔNG theo jdId → JD nhập tay vẫn có điểm khớp.
        Assert.NotNull(body.JdMatch);
        Assert.Equal(78, body.JdMatch!.Score);

        ai.Verify(x => x.AnalyzeAsync("BE", "Nội dung CV", "JD dán tay", It.IsAny<CancellationToken>()), Times.Once);

        var row = await t.Db.CvAnalyses.AsNoTracking().SingleAsync();
        Assert.Null(row.JdId);
        Assert.NotNull(row.JdMatch);   // jdMatch persist được (jsonb) dù không có file JD
    }

    // ── (e) cv-analysis: cả text lẫn file → text thắng, KHÔNG đọc file JD ──────────────────────
    [Fact]
    public async Task CvAnalysis_gui_ca_text_va_file_thi_text_thang()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var jdId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Nội dung CV"));
        storage.Setup(s => s.GetMetadata(jdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(jdId, user, "jd", "TU-FILE"));
        var ai = CvAiMock(new CvJdMatch(50, [], []));

        var result = await CvController(t, storage.Object, ai.Object, user)
            .Analyze(new CvAnalysisRequest(cvId, jdId, JobCategory.BE, JdText: "JD dán tay"), default);

        Assert.IsType<CreatedResult>(result);
        ai.Verify(x => x.AnalyzeAsync("BE", "Nội dung CV", "JD dán tay", It.IsAny<CancellationToken>()), Times.Once);
        // File JD không được đọc (khỏi round-trip + ownership-check cho file không dùng).
        storage.Verify(s => s.GetMetadata(jdId, It.IsAny<CancellationToken>()), Times.Never);

        var row = await t.Db.CvAnalyses.AsNoTracking().SingleAsync();
        Assert.Null(row.JdId);
    }
}
