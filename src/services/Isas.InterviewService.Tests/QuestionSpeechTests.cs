using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// TTS đọc câu hỏi thành tiếng — `GET api/practice/sessions/{sessionId}/questions/{questionId}/speech`
/// (qua gateway: /api/v1/interview/practice/sessions/.../speech).
///
/// Kiểm: owner-scope (INT-11) · câu hỏi phải THUỘC session · lỗi TTS → 502 không chặn luồng ·
/// KHÔNG gọi vendor khi request không hợp lệ (không đốt tiền). Cache theo nội dung nằm ở AIService
/// (tests/test_tts.py khoá phần "cache hit không gọi vendor").
/// </summary>
public class QuestionSpeechTests
{
    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 0x03, 0x00, 0x66, 0x61, 0x6B, 0x65];

    private static QuestionSpeechService BuildService(
        TestDb t, Mock<IAiServiceSpeechSynthesizer> synth)
        => new(t.Db, synth.Object);

    private static Mock<IAiServiceSpeechSynthesizer> Synth()
    {
        var m = new Mock<IAiServiceSpeechSynthesizer>();
        m.Setup(s => s.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuestionSpeech(Mp3, "audio/mpeg"));
        return m;
    }

    private static PracticeController BuildController(
        IQuestionSpeechService speech, Guid candidateId)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, candidateId.ToString())], "Test"));

        return new PracticeController(
            new Mock<IPracticeService>().Object, speech, NullLogger<PracticeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
    }

    // ── Đường chính: B2C ────────────────────────────────────────────────────────────
    [Fact]
    public async Task ChuBuoi_LayDuocAudioCauHoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var question = TestDb.Question(session.Id);
        t.Db.PracticeSessions.Add(session);
        t.Db.PracticeQuestions.Add(question);
        await t.Db.SaveChangesAsync();

        var synth = Synth();
        var result = await BuildService(t, synth)
            .GetQuestionSpeechAsync(candidate, session.Id, question.Id);

        Assert.NotNull(result);
        Assert.Equal(Mp3, result!.Content);
        Assert.Equal("audio/mpeg", result.ContentType);
        // Gửi ĐÚNG nội dung câu hỏi, nguyên văn (AI-4: dữ liệu, không nội suy thêm).
        synth.Verify(s => s.SynthesizeAsync(question.Content, "vi", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // B2B dùng CHUNG endpoint: session campaign cũng là practice_sessions + có candidate_id.
    [Fact]
    public async Task SessionB2B_CungDungDuocEndpointNay()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress,
            campaignId: Guid.NewGuid());
        var question = TestDb.Question(session.Id);
        t.Db.PracticeSessions.Add(session);
        t.Db.PracticeQuestions.Add(question);
        await t.Db.SaveChangesAsync();

        var result = await BuildService(t, Synth())
            .GetQuestionSpeechAsync(candidate, session.Id, question.Id);

        Assert.NotNull(result);
    }

    // ── Owner-scope (INT-11) ────────────────────────────────────────────────────────
    [Fact]
    public async Task NguoiKhac_KhongLayDuocAudio_VaKhongGoiVendor()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var session = TestDb.Session(owner, SessionStatus.Ready);
        var question = TestDb.Question(session.Id);
        t.Db.PracticeSessions.Add(session);
        t.Db.PracticeQuestions.Add(question);
        await t.Db.SaveChangesAsync();

        var synth = Synth();
        var svc = BuildService(t, synth);
        var intruder = Guid.NewGuid();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetQuestionSpeechAsync(intruder, session.Id, question.Id));

        // Không đọc trộm ĐÃ ĐÀNH, mà còn KHÔNG được tiêu tiền TTS cho request của kẻ lạ.
        synth.Verify(s => s.SynthesizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Controller_NguoiKhac_Tra403()
    {
        var speech = new Mock<IQuestionSpeechService>();
        speech.Setup(s => s.GetQuestionSpeechAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("Không phải buổi của bạn"));

        var result = await BuildController(speech.Object, Guid.NewGuid())
            .GetQuestionSpeech(Guid.NewGuid(), Guid.NewGuid(), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // ── Câu hỏi phải THUỘC session ──────────────────────────────────────────────────
    [Fact]
    public async Task CauHoiCuaBuoiKhac_TraNull_VaKhongGoiVendor()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var mine = TestDb.Session(candidate, SessionStatus.Ready);
        var other = TestDb.Session(candidate, SessionStatus.Ready);   // cùng chủ, KHÁC buổi
        var otherQuestion = TestDb.Question(other.Id);
        t.Db.PracticeSessions.AddRange(mine, other);
        t.Db.PracticeQuestions.Add(otherQuestion);
        await t.Db.SaveChangesAsync();

        var synth = Synth();

        // questionId có thật, nhưng KHÔNG thuộc buổi đang hỏi → 404 (không đọc trộm đề buổi khác).
        var result = await BuildService(t, synth)
            .GetQuestionSpeechAsync(candidate, mine.Id, otherQuestion.Id);

        Assert.Null(result);
        synth.Verify(s => s.SynthesizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SessionKhongTonTai_TraNull()
    {
        using var t = new TestDb();

        var result = await BuildService(t, Synth())
            .GetQuestionSpeechAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task Controller_KhongTimThay_Tra404()
    {
        var speech = new Mock<IQuestionSpeechService>();
        speech.Setup(s => s.GetQuestionSpeechAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QuestionSpeech?)null);

        var result = await BuildController(speech.Object, Guid.NewGuid())
            .GetQuestionSpeech(Guid.NewGuid(), Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── Lỗi TTS → 502, KHÔNG chặn luồng phỏng vấn ───────────────────────────────────
    [Fact]
    public async Task Controller_AiServiceLoi_Tra502()
    {
        var speech = new Mock<IQuestionSpeechService>();
        speech.Setup(s => s.GetQuestionSpeechAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /tts trả 502"));

        var result = await BuildController(speech.Object, Guid.NewGuid())
            .GetQuestionSpeech(Guid.NewGuid(), Guid.NewGuid(), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
    }

    // Guard tĩnh: endpoint /speech KHÔNG được mang [Produces]. ProducesAttribute ghi đè
    // ObjectResult.ContentTypes cho MỌI kết quả của action — nhánh 403/404/502 trả body JSON sẽ
    // không có formatter nào ghi nổi dưới audio/mpeg → client nhận 406 thay vì mã lỗi thật.
    // Unit test gọi thẳng action KHÔNG bắt được (không chạy result-filter) nên khoá bằng reflection.
    [Fact]
    public void EndpointSpeech_KhongDuocMang_ProducesAttribute()
    {
        var action = typeof(PracticeController).GetMethod(nameof(PracticeController.GetQuestionSpeech));

        Assert.NotNull(action);
        Assert.Null(action!.GetCustomAttributes(typeof(ProducesAttribute), inherit: true)
            .Cast<ProducesAttribute>().FirstOrDefault());
    }

    // Vendor chết → ném AiServiceException lên controller, KHÔNG nuốt thành "không tìm thấy" (404):
    // 404 sẽ khiến FE tưởng câu hỏi không tồn tại thay vì hiểu là TTS tạm hỏng và degrade về chữ.
    [Fact]
    public async Task VendorLoi_NemAiServiceException_KhongNuotThanh404()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready);
        var question = TestDb.Question(session.Id);
        t.Db.PracticeSessions.Add(session);
        t.Db.PracticeQuestions.Add(question);
        await t.Db.SaveChangesAsync();

        var synth = new Mock<IAiServiceSpeechSynthesizer>();
        synth.Setup(s => s.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("Gemini 503"));

        await Assert.ThrowsAsync<AiServiceException>(
            () => BuildService(t, synth).GetQuestionSpeechAsync(candidate, session.Id, question.Id));
    }
}
