using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Validation;
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
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            "BE", null, "Tuyển BE Java 3 năm", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        // Không có file nào phải parse.
        storage.Verify(s => s.GetOwnedParsedTextAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

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
        storage.Setup(s => s.GetOwnedParsedTextAsync(jdId, candidate, It.IsAny<CancellationToken>())).ReturnsAsync("TU-FILE");
        var gen = GeneratorMock();

        var res = await BuildPractice(t, storage.Object, gen.Object).CreateSessionAsync(
            candidate,
            new CreatePracticeSessionRequest(null, jdId, JobCategory.BE, JdText: "JD nhập tay"));

        gen.Verify(g => g.GenerateQuestionsAsync(
            "BE", null, "JD nhập tay", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(s => s.GetOwnedParsedTextAsync(jdId, candidate, It.IsAny<CancellationToken>()), Times.Never);

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
        storage.Setup(s => s.GetOwnedParsedTextAsync(jdId, candidate, It.IsAny<CancellationToken>())).ReturnsAsync("TU-FILE");
        var gen = GeneratorMock();

        var res = await BuildPractice(t, storage.Object, gen.Object).CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, jdId, JobCategory.BE, JdText: "   "));

        gen.Verify(g => g.GenerateQuestionsAsync(
            "BE", null, "TU-FILE", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

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

    // ── (f) Cap độ dài jdText (TextInputLimits.JdTextMaxChars — CÙNG ngưỡng với B2B/Campaign) ──
    // JD nhập tay đi thẳng vào prompt Gemini → chặn ở BE, và phải chặn TRƯỚC reserve credit (PAY-5).

    // (f) Tạo buổi luyện, SÁT ngưỡng (đúng JdTextMaxChars ký tự) → VẪN QUA ("tối đa", không phải "nhỏ hơn").
    [Fact]
    public async Task Create_session_jdText_sat_nguong_van_qua()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        var gen = GeneratorMock();
        var atLimit = new string('x', TextInputLimits.JdTextMaxChars);

        var res = await BuildPractice(t, storage.Object, gen.Object).CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE, JdText: atLimit));

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        gen.Verify(g => g.GenerateQuestionsAsync("BE", null, atLimit, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // (f) Tạo buổi luyện, VƯỢT ngưỡng → 400 (InvalidOperationException) TRƯỚC reserve/AI/session:
    // KHÔNG giữ credit oan, KHÔNG có row session, KHÔNG gọi Gemini.
    [Fact]
    public async Task Create_session_jdText_vuot_nguong_thi_400_truoc_reserve()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        var gen = GeneratorMock();
        var credits = CreditsMock();
        var svc = new PracticeService(
            t.Db, storage.Object, gen.Object, new Mock<ISessionScoringNotifier>().Object,
            credits.Object, NullLogger<PracticeService>.Instance);

        var tooLong = new string('x', TextInputLimits.JdTextMaxChars + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE, JdText: tooLong)));

        Assert.Contains(TextInputLimits.JdTextMaxChars.ToString(), ex.Message);
        Assert.Contains((TextInputLimits.JdTextMaxChars + 1).ToString(), ex.Message);   // độ dài đang gửi

        // Guard chạy TRƯỚC mọi tác dụng phụ tốn tiền / để lại rác.
        credits.Verify(c => c.ReserveAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        gen.Verify(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await t.Db.PracticeSessions.AsNoTracking().ToListAsync());
    }

    // (f) cv-analysis, VƯỢT ngưỡng → 400 TRƯỚC cả đọc CV lẫn reserve (mẫu BK6: guard rẻ nhất chạy đầu).
    [Fact]
    public async Task CvAnalysis_jdText_vuot_nguong_thi_400_truoc_doc_CV_va_reserve()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        var ai = CvAiMock(null);
        var credits = CreditsMock();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:CvAnalysisCredits"] = "1"
        }).Build();
        var svc = new CvAnalysisService(
            t.Db, storage.Object, ai.Object, credits.Object, config, NullLogger<CvAnalysisService>.Instance);

        var tooLong = new string('x', TextInputLimits.JdTextMaxChars + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AnalyzeAsync(
            user, new CvAnalysisRequest(cvId, null, JobCategory.BE, JdText: tooLong)));

        Assert.Contains(TextInputLimits.JdTextMaxChars.ToString(), ex.Message);

        credits.Verify(c => c.ReserveAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        storage.Verify(s => s.GetMetadata(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        ai.Verify(x => x.AnalyzeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await t.Db.CvAnalyses.AsNoTracking().ToListAsync());
    }

    // (f) cv-analysis, SÁT ngưỡng → VẪN QUA (ngưỡng đo SAU trim nên khoảng trắng thừa không bị tính).
    [Fact]
    public async Task CvAnalysis_jdText_sat_nguong_kem_khoang_trang_thua_van_qua()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Nội dung CV"));
        var ai = CvAiMock(new CvJdMatch(70, [], []));

        var atLimit = new string('x', TextInputLimits.JdTextMaxChars);
        var padded = "  " + atLimit + "  \n";

        var result = await CvController(t, storage.Object, ai.Object, user)
            .Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE, JdText: padded), default);

        Assert.IsType<CreatedResult>(result);
        ai.Verify(x => x.AnalyzeAsync("BE", "Nội dung CV", atLimit, It.IsAny<CancellationToken>()), Times.Once);
    }
}
