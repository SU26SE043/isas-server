using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// ATT1-B3 — đồng hồ cả buổi B2B: vào phòng (begin), che đề trước begin, chặn upload/speech, ân hạn
/// 30 giây dùng CHUNG cho upload và sweeper. Hợp đồng [I1]–[I5].
///
/// <para>Buổi KHÔNG tính giờ (B2C · B2B tạo trước ATT1 = DurationMinutes null) phải chạy y hệt hôm nay —
/// các test "KhongTinhGio_*" khoá điều đó, ngoài 1890 test cũ (không sửa assert).</para>
/// </summary>
public class SessionTimingAtt1Tests
{
    private readonly ITestOutputHelper _out;
    public SessionTimingAtt1Tests(ITestOutputHelper output) => _out = output;

    private const int Duration = 15;

    // Cùng cấu hình JSON với Program.cs (AddControllers().AddJsonOptions(...)): mặc định MVC = Web
    // (camelCase) + 2 converter. Dùng để (a) bind body JSON THẬT như ASP.NET làm, (b) dựng ví dụ JSON
    // response đúng như client nhận.
    private static JsonSerializerOptions MvcJson()
    {
        var o = new Microsoft.AspNetCore.Mvc.JsonOptions().JsonSerializerOptions;
        o.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new UtcDateTimeConverter());
        return o;
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value, MvcJson());

    // ── dựng dữ liệu ─────────────────────────────────────────────────────────────────────────

    private static (PracticeSession S, PracticeQuestion Q1, PracticeQuestion Q2) SeedSession(
        TestDb t, Guid candidate, Guid? campaignId, int? duration, DateTime? deadline,
        SessionStatus status = SessionStatus.Ready, DateTime? begunAt = null, DateTime? createdAt = null)
    {
        var s = TestDb.Session(candidate, status, campaignId: campaignId, createdAt: createdAt, deadline: deadline);
        s.DurationMinutes = duration;
        s.BegunAt = begunAt;
        var q1 = TestDb.Question(s.Id, 1);
        q1.Content = "Câu một: thiết kế hàng đợi?";
        var q2 = TestDb.Question(s.Id, 2);
        q2.Content = "Câu hai: idempotency là gì?";
        t.Db.AddRange(s, q1, q2);
        t.Db.SaveChanges();
        return (s, q1, q2);
    }

    private static PracticeService Practice(InterviewDbContext db)
    {
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return new PracticeService(
            db, new Mock<IStorageService>().Object, new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);
    }

    private static ControllerContext As(Guid candidate) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, candidate.ToString())], "test"))
        }
    };

    private static PracticeController PracticeCtl(
        InterviewDbContext db, Guid candidate, IQuestionSpeechService? speech = null)
        => new(Practice(db), speech ?? new Mock<IQuestionSpeechService>().Object,
            NullLogger<PracticeController>.Instance) { ControllerContext = As(candidate) };

    private static (AnswerService Svc, Mock<IStorageService> Storage) Answers(
        InterviewDbContext db, int? graceSeconds = null)
    {
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/x.webm");
        var notifier = new Mock<ISessionScoringNotifier>();
        var svc = new AnswerService(
            db, storage.Object, new Mock<IScoringJobPublisher>().Object, notifier.Object,
            TestDb.ScoringOpts(), NullLogger<AnswerService>.Instance,
            sessionDeadlineOptions: graceSeconds is int g
                ? Options.Create(new SessionDeadlineOptions { GraceSeconds = g })
                : null);
        return (svc, storage);
    }

    private static Task<UploadAnswerResult> Upload(AnswerService svc, PracticeSession s, Guid questionId)
    {
        var audio = new MemoryStream(new byte[] { 1, 2, 3 });
        return svc.UploadAnswerAsync(s.Id, questionId, s.CandidateId, audio, "audio/webm", 20);
    }

    private static PracticeSession Reload(TestDb t, Guid id)
        => t.NewContext().PracticeSessions.AsNoTracking().Single(x => x.Id == id);

    private static (int Status, string? Code) ConflictOf(IActionResult r)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(r);
        using var doc = JsonDocument.Parse(Json(obj.Value));
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        Assert.True(doc.RootElement.TryGetProperty("error", out _), "body phải có khoá `error`");
        return (obj.StatusCode ?? 0, code);
    }

    // ══ [I1] BEGIN ════════════════════════════════════════════════════════════════════════════

    // Theory nhiều thời lượng: mutation "begin dùng hằng 15" từng XANH vì mọi test cũ đều dùng đúng 15
    // phút — thời lượng phải đến từ CHÍNH buổi, không phải con số trùng với fixture.
    [Theory]
    [InlineData(5)]
    [InlineData(45)]
    [InlineData(180)]
    public async Task Begin_TinhGio_HanCungXa_DeadlineBangNowCongThoiLuong_KhongDoiStatus(int duration)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, candidate, Guid.NewGuid(), duration, DateTime.UtcNow.AddDays(7));

        var before = DateTime.UtcNow;
        var res = await Practice(t.NewContext()).BeginSessionAsync(candidate, s.Id);
        var after = DateTime.UtcNow;

        var saved = Reload(t, s.Id);
        Assert.NotNull(saved.BegunAt);
        Assert.InRange(saved.BegunAt!.Value, before, after);
        Assert.Equal(saved.BegunAt.Value.AddMinutes(duration), saved.Deadline);   // không bị hạn cứng chặn
        Assert.Equal(SessionStatus.Ready, saved.Status);                           // CẤM đổi Status ở begin

        Assert.Equal(s.Id, res.SessionId);
        Assert.Equal(saved.BegunAt, res.BeganAt);
        Assert.Equal(saved.Deadline, res.Deadline);
        Assert.Equal(duration, res.DurationMinutes);
        Assert.InRange(res.ServerNow, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Begin_TinhGio_HanCungGanHon_DeadlineBangHanCung()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var hard = DateTime.UtcNow.AddMinutes(5);   // gần hơn now + 15'
        var (s, _, _) = SeedSession(t, candidate, Guid.NewGuid(), Duration, hard);

        var res = await Practice(t.NewContext()).BeginSessionAsync(candidate, s.Id);

        var saved = Reload(t, s.Id);
        Assert.Equal(hard, saved.Deadline);   // begin KHÔNG được kéo dài quá hạn cứng
        Assert.Equal(hard, res.Deadline);
        Assert.NotNull(saved.BegunAt);
    }

    [Fact]
    public async Task Begin_TinhGio_KhongCoHanCung_DeadlineBangNowCongThoiLuong()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, candidate, Guid.NewGuid(), Duration, deadline: null);

        await Practice(t.NewContext()).BeginSessionAsync(candidate, s.Id);

        var saved = Reload(t, s.Id);
        Assert.Equal(saved.BegunAt!.Value.AddMinutes(Duration), saved.Deadline);
    }

    [Fact]
    public async Task Begin_HaiLan_BegunAtVaDeadlineKhongDoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, candidate, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));

        var first = await Practice(t.NewContext()).BeginSessionAsync(candidate, s.Id);
        var savedFirst = Reload(t, s.Id);
        await Task.Delay(20);   // now đã trôi: lần hai mà ghi đè thì mốc sẽ khác
        var second = await Practice(t.NewContext()).BeginSessionAsync(candidate, s.Id);
        var savedSecond = Reload(t, s.Id);

        Assert.Equal(first.BeganAt, second.BeganAt);
        Assert.Equal(first.Deadline, second.Deadline);
        Assert.Equal(savedFirst.BegunAt, savedSecond.BegunAt);
        Assert.Equal(savedFirst.Deadline, savedSecond.Deadline);
        Assert.Equal(Duration, second.DurationMinutes);
    }

    // Hai tab cùng bấm: bên này đọc BegunAt = null, nhưng NGAY TRƯỚC câu UPDATE thì tab kia đã ghi.
    // ExecuteUpdate có điều kiện begun_at IS NULL ⇒ 0 dòng ⇒ đọc lại ⇒ trả đúng giá trị bên thắng.
    [Fact]
    public async Task Begin_HaiTabCungBam_BenThuaDocLaiGiaTriBenThang()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, candidate, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));

        var winnerAt = DateTime.UtcNow.AddSeconds(-3);
        var winnerDeadline = winnerAt.AddMinutes(Duration);
        var interceptor = new WinnerFirstInterceptor(async () =>
        {
            using var other = t.NewContext();
            await other.PracticeSessions.Where(x => x.Id == s.Id)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.BegunAt, winnerAt)
                    .SetProperty(x => x.Deadline, winnerDeadline));
        });

        using var db = t.NewContext(null, [interceptor]);
        var res = await Practice(db).BeginSessionAsync(candidate, s.Id);

        Assert.True(interceptor.Fired, "khe đua không được tái hiện — test vô nghĩa");
        Assert.Equal(winnerAt, res.BeganAt);
        Assert.Equal(winnerDeadline, res.Deadline);
        var saved = Reload(t, s.Id);
        Assert.Equal(winnerAt, saved.BegunAt);
        Assert.Equal(winnerDeadline, saved.Deadline);
    }

    [Fact]
    public async Task Begin_B2C_200_BeganAtNull_DeadlineGiuNguyen()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, candidate, campaignId: null, duration: null, deadline: null);

        var result = await PracticeCtl(t.NewContext(), candidate).BeginSession(s.Id, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var res = Assert.IsType<BeginSessionResponse>(ok.Value);
        Assert.Null(res.BeganAt);
        Assert.Null(res.DurationMinutes);
        Assert.Null(res.Deadline);
        var saved = Reload(t, s.Id);
        Assert.Null(saved.BegunAt);
        Assert.Null(saved.Deadline);
        Assert.Equal(SessionStatus.Ready, saved.Status);
    }

    [Fact]
    public async Task Begin_B2BKhongTinhGio_200_DeadlineGiuNguyen_KhongGhiBegunAt()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var hard = DateTime.UtcNow.AddDays(2);
        var (s, _, _) = SeedSession(t, candidate, Guid.NewGuid(), duration: null, deadline: hard);

        var res = await Practice(t.NewContext()).BeginSessionAsync(candidate, s.Id);

        Assert.Null(res.BeganAt);
        Assert.Null(res.DurationMinutes);
        Assert.Equal(hard, res.Deadline);
        var saved = Reload(t, s.Id);
        Assert.Null(saved.BegunAt);
        Assert.Equal(hard, saved.Deadline);
    }

    [Theory]
    [InlineData(SessionStatus.Scored)]
    [InlineData(SessionStatus.SessionAbandoned)]
    [InlineData(SessionStatus.Scoring)]
    [InlineData(SessionStatus.Completed)]
    [InlineData(SessionStatus.Failed)]
    public async Task Begin_BuoiDaKetThuc_409_SessionEnded(SessionStatus status)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, candidate, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7), status);

        var result = await PracticeCtl(t.NewContext(), candidate).BeginSession(s.Id, default);

        var (code, errCode) = ConflictOf(result);
        Assert.Equal(StatusCodes.Status409Conflict, code);
        Assert.Equal("SESSION_ENDED", errCode);
        Assert.Null(Reload(t, s.Id).BegunAt);
    }

    [Fact]
    public async Task Begin_NguoiKhac_403_KhongGhiGi()
    {
        using var t = new TestDb();
        var (s, _, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));

        var result = await PracticeCtl(t.NewContext(), Guid.NewGuid()).BeginSession(s.Id, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        Assert.Null(Reload(t, s.Id).BegunAt);
    }

    [Fact]
    public async Task Begin_KhongTonTai_404()
    {
        using var t = new TestDb();
        var result = await PracticeCtl(t.NewContext(), Guid.NewGuid()).BeginSession(Guid.NewGuid(), default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ══ [I2] GET session ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_TruocBegin_ContentRong_QuestionsLocked_SauBegin_ContentDu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, q1, q2) = SeedSession(t, candidate, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));

        var before = await Practice(t.NewContext()).GetSessionAsync(candidate, s.Id);
        Assert.NotNull(before);
        Assert.True(before!.QuestionsLocked);
        Assert.All(before.Questions, q => Assert.Equal("", q.Content));
        Assert.Equal(new[] { q1.Id, q2.Id }, before.Questions.Select(q => q.Id));   // khung câu vẫn đủ
        Assert.Equal(Duration, before.DurationMinutes);
        Assert.Null(before.BeganAt);
        Assert.NotNull(before.ServerNow);

        await Practice(t.NewContext()).BeginSessionAsync(candidate, s.Id);
        var after = await Practice(t.NewContext()).GetSessionAsync(candidate, s.Id);

        Assert.False(after!.QuestionsLocked);
        Assert.Equal(new[] { q1.Content, q2.Content }, after.Questions.Select(q => q.Content));
        Assert.NotNull(after.BeganAt);
        Assert.Equal(Reload(t, s.Id).Deadline, after.Deadline);
    }

    // CẤM 409 ở GET khi chưa begin — trang chuẩn bị gọi GET trước begin.
    [Fact]
    public async Task Get_TruocBegin_QuaController_200()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, candidate, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));

        var result = await PracticeCtl(t.NewContext(), candidate).GetSession(s.Id, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(Json(ok.Value));
        Assert.True(doc.RootElement.GetProperty("questionsLocked").GetBoolean());
        Assert.All(doc.RootElement.GetProperty("questions").EnumerateArray(),
            q => Assert.Equal("", q.GetProperty("content").GetString()));
    }

    [Fact]
    public async Task NoiBoHr_RevealCampaignScoring_KhongBiCheDuChuaBegin()
    {
        using var t = new TestDb();
        var (s, q1, q2) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));

        var questions = await Practice(t.NewContext()).GetSessionAnswersInternalAsync(s.Id);

        Assert.Equal(new[] { q1.Content, q2.Content }, questions!.Select(q => q.Content));
    }

    [Fact]
    public async Task KhongTinhGio_B2C_VaB2BTruocAtt1_GetKhongBiChe()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (b2c, b2cQ1, _) = SeedSession(t, candidate, null, null, null);
        var (b2b, b2bQ1, _) = SeedSession(t, candidate, Guid.NewGuid(), null, DateTime.UtcNow.AddDays(1));

        foreach (var (id, firstContent) in new[] { (b2c.Id, b2cQ1.Content), (b2b.Id, b2bQ1.Content) })
        {
            var r = await Practice(t.NewContext()).GetSessionAsync(candidate, id);
            Assert.False(r!.QuestionsLocked);
            Assert.Equal(firstContent, r.Questions[0].Content);
            Assert.Null(r.DurationMinutes);
            Assert.Null(r.BeganAt);
        }
    }

    // ══ [I3] Upload ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Upload_TruocBegin_SessionNotBegun_KhongTaiAudioLen()
    {
        using var t = new TestDb();
        var (s, q1, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));
        var (svc, storage) = Answers(t.NewContext());

        await Assert.ThrowsAsync<SessionNotBegunException>(() => Upload(svc, s, q1.Id));

        storage.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(t.NewContext().PracticeAnswers.Where(a => a.SessionId == s.Id));
    }

    [Fact]
    public async Task Upload_Deadline29GiayTruoc_VanNhan()
    {
        using var t = new TestDb();
        var (s, q1, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration,
            deadline: DateTime.UtcNow.AddSeconds(-29), begunAt: DateTime.UtcNow.AddMinutes(-Duration));
        var (svc, _) = Answers(t.NewContext());

        var res = await Upload(svc, s, q1.Id);

        Assert.Equal(q1.Id, res.QuestionId);
        Assert.Single(t.NewContext().PracticeAnswers.Where(a => a.SessionId == s.Id));
    }

    [Fact]
    public async Task Upload_Deadline31GiayTruoc_409SessionTimeUp()
    {
        using var t = new TestDb();
        var (s, q1, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration,
            deadline: DateTime.UtcNow.AddSeconds(-31), begunAt: DateTime.UtcNow.AddMinutes(-Duration));
        var (svc, storage) = Answers(t.NewContext());

        await Assert.ThrowsAsync<SessionTimeUpException>(() => Upload(svc, s, q1.Id));
        storage.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Ân hạn đi từ OPTION, không phải hằng số chôn trong code: đổi option là cả hai đầu đổi theo.
    [Fact]
    public async Task Upload_AnHanLayTuOption_Grace120_Deadline90GiayTruoc_VanNhan()
    {
        using var t = new TestDb();
        var (s, q1, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration,
            deadline: DateTime.UtcNow.AddSeconds(-90), begunAt: DateTime.UtcNow.AddMinutes(-Duration));
        var (svc, _) = Answers(t.NewContext(), graceSeconds: 120);

        await Upload(svc, s, q1.Id);

        Assert.Single(t.NewContext().PracticeAnswers.Where(a => a.SessionId == s.Id));
    }

    // Buổi B2B tạo TRƯỚC ATT1 (DurationMinutes null): luật chốt "hành vi giữ nguyên" — hôm nay upload chỉ
    // chặn theo Status, không theo Deadline (sweeper lo chốt buổi). ATT1 không được đổi điều đó.
    [Fact]
    public async Task KhongTinhGio_B2BTruocAtt1_QuaHanCung_UploadVanNhanNhuHomNay()
    {
        using var t = new TestDb();
        var (s, q1, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), duration: null,
            deadline: DateTime.UtcNow.AddMinutes(-5), status: SessionStatus.InProgress);
        var (svc, _) = Answers(t.NewContext());

        await Upload(svc, s, q1.Id);

        Assert.Single(t.NewContext().PracticeAnswers.Where(a => a.SessionId == s.Id));
    }

    [Fact]
    public async Task KhongTinhGio_B2C_UploadKhongCanBegin()
    {
        using var t = new TestDb();
        var (s, q1, _) = SeedSession(t, Guid.NewGuid(), null, null, null);
        var (svc, _) = Answers(t.NewContext());

        await Upload(svc, s, q1.Id);

        Assert.Equal(SessionStatus.InProgress, Reload(t, s.Id).Status);
    }

    // Khe nối service → controller: lỗi đi tới client là 409 CÓ MÃ (không bị catch
    // InvalidOperationException nuốt thành 409 không mã).
    [Theory]
    [InlineData(false, "SESSION_NOT_BEGUN")]
    [InlineData(true, "SESSION_TIME_UP")]
    public async Task UploadController_409CoMa(bool begunAndExpired, string expectedCode)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, q1, _) = begunAndExpired
            ? SeedSession(t, candidate, Guid.NewGuid(), Duration, DateTime.UtcNow.AddMinutes(-2),
                begunAt: DateTime.UtcNow.AddMinutes(-20))
            : SeedSession(t, candidate, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audio:StrictFormatGate"] = "false"
        }).Build();
        var (svc, _) = Answers(t.NewContext());
        var controller = new AnswersController(svc, config, NullLogger<AnswersController>.Instance)
        {
            ControllerContext = As(candidate)
        };
        var bytes = new byte[] { 1, 2, 3, 4 };
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "a.webm")
        {
            Headers = new HeaderDictionary(), ContentType = "audio/webm"
        };

        var result = await controller.Upload(s.Id, q1.Id, file, 20, default);

        var (status, code) = ConflictOf(result);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void AnHan_BienDung_DeadlineCongGrace_VanNhan_QuaMotTick_TuChoi()
    {
        var d = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);
        var grace = TimeSpan.FromSeconds(30);

        Assert.False(SessionTiming.IsPastGrace(d, d + grace, grace));                 // đúng mốc: còn nhận
        Assert.True(SessionTiming.IsPastGrace(d, d + grace + TimeSpan.FromTicks(1), grace));
        Assert.False(SessionTiming.IsPastGrace(d, d.AddSeconds(29), grace));
        Assert.False(SessionTiming.IsPastGrace(null, d.AddDays(9), grace));            // không hạn ⇒ không chặn
        // Sweeper cùng nghĩa: deadline < now − grace ⇔ deadline + grace < now.
        Assert.False(d < SessionTiming.SweepCutoff(d + grace, grace));
        Assert.True(d < SessionTiming.SweepCutoff(d + grace + TimeSpan.FromTicks(1), grace));
    }

    [Fact]
    public void AnHan_MacDinhTrongCode_30Giay()
    {
        Assert.Equal(30, new SessionDeadlineOptions().GraceSeconds);
        Assert.Equal(TimeSpan.FromSeconds(30), new SessionDeadlineOptions().Grace);
    }

    // ══ [I4] Speech ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Speech_TruocBegin_409SessionNotBegun_KhongGoiTts_SauBegin_DocDuoc()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, q1, _) = SeedSession(t, candidate, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));
        var tts = new Mock<IAiServiceSpeechSynthesizer>();
        tts.Setup(x => x.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuestionSpeech(new byte[] { 9 }, "audio/mpeg"));

        var locked = await PracticeCtl(t.NewContext(), candidate,
            new QuestionSpeechService(t.NewContext(), tts.Object)).GetQuestionSpeech(s.Id, q1.Id, default);

        var (status, code) = ConflictOf(locked);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("SESSION_NOT_BEGUN", code);
        tts.Verify(x => x.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await Practice(t.NewContext()).BeginSessionAsync(candidate, s.Id);
        var open = await PracticeCtl(t.NewContext(), candidate,
            new QuestionSpeechService(t.NewContext(), tts.Object)).GetQuestionSpeech(s.Id, q1.Id, default);

        Assert.IsType<FileContentResult>(open);
    }

    // ══ [I5] Sweeper ══════════════════════════════════════════════════════════════════════════

    private static async Task ScanOnce(SessionAbandonSweeper sweeper)
    {
        var mi = typeof(SessionAbandonSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(sweeper, new object[] { CancellationToken.None })!;
    }

    private static SessionAbandonSweeper Sweeper(TestDb t, int? graceSeconds = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var notifier = new Mock<ISessionScoringNotifier>();
        services.AddSingleton(new Mock<IStorageService>().Object);
        services.AddSingleton(new Mock<IAiServiceQuestionGenerator>().Object);
        services.AddSingleton(notifier.Object);
        services.AddSingleton(new Mock<ICreditReservationClient>().Object);
        services.AddScoped<IPracticeService, PracticeService>();
        var provider = services.BuildServiceProvider();

        return new SessionAbandonSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ScoringOptions()),
            NullLogger<SessionAbandonSweeper>.Instance,
            graceSeconds is int g ? Options.Create(new SessionDeadlineOptions { GraceSeconds = g }) : null);
    }

    private static void AddScoredAnswer(TestDb t, PracticeSession s, PracticeQuestion q)
    {
        t.Db.Add(TestDb.Answer(s.Id, q.Id, AnswerStatus.Scored,
            DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-5)));
        t.Db.SaveChanges();
    }

    [Fact]
    public async Task Sweeper_Deadline29GiayTruoc_ChuaChot_CoAnswer()
    {
        using var t = new TestDb();
        var (s, q1, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration,
            DateTime.UtcNow.AddSeconds(-29), SessionStatus.InProgress, DateTime.UtcNow.AddMinutes(-Duration));
        AddScoredAnswer(t, s, q1);

        await ScanOnce(Sweeper(t));

        Assert.Equal(SessionStatus.InProgress, Reload(t, s.Id).Status);
    }

    [Fact]
    public async Task Sweeper_Deadline31GiayTruoc_CoAnswer_TuNop()
    {
        using var t = new TestDb();
        var (s, q1, q2) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration,
            DateTime.UtcNow.AddSeconds(-31), SessionStatus.InProgress, DateTime.UtcNow.AddMinutes(-Duration));
        AddScoredAnswer(t, s, q1);

        await ScanOnce(Sweeper(t));

        var saved = Reload(t, s.Id);
        Assert.Equal(SessionStatus.Scored, saved.Status);   // tự nộp; câu trống ⇒ Skipped
        Assert.Contains(t.NewContext().PracticeAnswers.AsNoTracking().Where(a => a.SessionId == s.Id),
            a => a.QuestionId == q2.Id && a.Status == AnswerStatus.Skipped);
    }

    [Fact]
    public async Task Sweeper_Deadline29GiayTruoc_KhongAnswer_ChuaChot()
    {
        using var t = new TestDb();
        var (s, _, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration,
            DateTime.UtcNow.AddSeconds(-29), SessionStatus.Ready, DateTime.UtcNow.AddMinutes(-Duration));

        await ScanOnce(Sweeper(t));

        Assert.Equal(SessionStatus.Ready, Reload(t, s.Id).Status);
        Assert.Null(TestDb.AbandonedOutbox(t.NewContext(), s.Id));
    }

    [Fact]
    public async Task Sweeper_Deadline31GiayTruoc_KhongAnswer_ExpiredNoAnswer()
    {
        using var t = new TestDb();
        var (s, _, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration,
            DateTime.UtcNow.AddSeconds(-31), SessionStatus.Ready, DateTime.UtcNow.AddMinutes(-Duration));

        await ScanOnce(Sweeper(t));

        Assert.Equal(SessionStatus.SessionAbandoned, Reload(t, s.Id).Status);
        Assert.Equal("expired_no_answer", TestDb.AbandonedOutbox(t.NewContext(), s.Id)!.Reason);
    }

    [Fact]
    public async Task Sweeper_AnHanLayTuOption_Grace120_Deadline90GiayTruoc_ChuaChot()
    {
        using var t = new TestDb();
        var (s, _, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration,
            DateTime.UtcNow.AddSeconds(-90), SessionStatus.Ready, DateTime.UtcNow.AddMinutes(-Duration));

        await ScanOnce(Sweeper(t, graceSeconds: 120));

        Assert.Equal(SessionStatus.Ready, Reload(t, s.Id).Status);
    }

    // ══ Hợp đồng dây Campaign → Interview: khoá JSON "durationMinutes" ════════════════════════

    private const string InternalJsonBase =
        """{"candidateId":"11111111-1111-1111-1111-111111111111","campaignId":"22222222-2222-2222-2222-222222222222","orgId":"33333333-3333-3333-3333-333333333333","jobCategory":"BE","questions":["Q1","Q2"],"criteria":[{"name":"Communication","weight":1.0,"maxScore":5}],"expiresAt":"2030-01-01T00:00:00Z","skipPenalty":true""";

    private static async Task<PracticeSession> PostInternal(TestDb t, string json)
    {
        // Bind ĐÚNG như ASP.NET: body JSON thật → CreateCampaignSessionInternalRequest bằng cấu hình MVC
        // của Program.cs; rồi đi qua controller + PracticeService THẬT xuống DB.
        var req = JsonSerializer.Deserialize<CreateCampaignSessionInternalRequest>(json, MvcJson())!;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tok" }).Build();
        var controller = new InternalSessionsController(
            Practice(t.NewContext()), config, NullLogger<InternalSessionsController>.Instance);

        var result = await controller.CreateOrGetCampaignSession(req, "tok", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<PracticeSessionResponse>(ok.Value);
        return Reload(t, body.Id);
    }

    [Fact]
    public async Task DayNoiBo_JsonThat_DurationMinutes_GhiVaoSession_DeadlineVanLaHanCung()
    {
        using var t = new TestDb();

        var saved = await PostInternal(t, InternalJsonBase + ""","durationMinutes":15}""");

        Assert.Equal(15, saved.DurationMinutes);
        Assert.Null(saved.BegunAt);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), saved.Deadline);   // chưa begin
        Assert.True(SessionTiming.IsLocked(saved));
    }

    [Fact]
    public async Task DayNoiBo_JsonThat_KhongCoKhoa_KhongTinhGio()
    {
        using var t = new TestDb();

        var saved = await PostInternal(t, InternalJsonBase + "}");

        Assert.Null(saved.DurationMinutes);
        Assert.False(SessionTiming.IsLocked(saved));
    }

    [Theory]
    [InlineData(100000, 180)]
    [InlineData(0, 5)]
    [InlineData(-30, 5)]
    [InlineData(5, 5)]
    [InlineData(180, 180)]
    public async Task DayNoiBo_ThoiLuongNgoaiMien_KepVe5Den180_KhongVoCheck(int sent, int expected)
    {
        using var t = new TestDb();

        var saved = await PostInternal(t, InternalJsonBase + $$""","durationMinutes":{{sent}}}""");

        Assert.Equal(expected, saved.DurationMinutes);
    }

    // ══ KIỂM B3 — 6 lỗ test (mutation XANH) do người kiểm chỉ ra; assert giữ nguyên probe ═════════

    // [I1] "Gọi lại = trả CÙNG beganAt/deadline" — kể cả khi buổi đã InProgress (tải lại trang GIỮA bài).
    // Khoá: IsEnded không được nuốt InProgress (KIỂM R2).
    [Fact]
    public async Task Begin_GoiLaiGiuaBai_BuoiInProgressDaVaoPhong_200_CungMoc()
    {
        using var t = new TestDb();
        var c = Guid.NewGuid();
        var begun = DateTime.UtcNow.AddMinutes(-5);
        var deadline = begun.AddMinutes(15);
        var (s, _, _) = SeedSession(t, c, Guid.NewGuid(), Duration, deadline, SessionStatus.InProgress, begun);

        var r = await PracticeCtl(t.NewContext(), c).BeginSession(s.Id, default);

        var ok = Assert.IsType<OkObjectResult>(r);
        var body = Assert.IsType<BeginSessionResponse>(ok.Value);
        Assert.Equal(begun, body.BeganAt);
        Assert.Equal(deadline, body.Deadline);
    }

    // CẤM vượt hạn cứng — kể cả khi hạn cứng ĐÃ QUA (sweeper chưa kịp chạy) lúc ứng viên mới vào phòng:
    // Deadline giữ hạn cứng, không thành now + thời lượng (KIỂM R3).
    [Fact]
    public async Task Begin_HanCungDaQua_DeadlineGiuHanCung()
    {
        using var t = new TestDb();
        var c = Guid.NewGuid();
        var hard = DateTime.UtcNow.AddMinutes(-10);
        var (s, _, _) = SeedSession(t, c, Guid.NewGuid(), Duration, hard, SessionStatus.Ready, begunAt: null);

        await Practice(t.NewContext()).BeginSessionAsync(c, s.Id);

        Assert.Equal(hard, Reload(t, s.Id).Deadline);
    }

    // [I2] serverNow = giờ server LÚC TRẢ LỜI (client tính còn lại = deadline − serverNow), không phải
    // một mốc lưu trong DB (KIỂM R6).
    [Fact]
    public async Task Get_ServerNow_LaGioServerLucTraLoi()
    {
        using var t = new TestDb();
        var c = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, c, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(1),
            SessionStatus.Ready, begunAt: null, createdAt: DateTime.UtcNow.AddDays(-2));

        var before = DateTime.UtcNow;
        var r = await Practice(t.NewContext()).GetSessionAsync(c, s.Id);
        var after = DateTime.UtcNow;

        Assert.NotNull(r!.ServerNow);
        Assert.InRange(r.ServerNow!.Value, before, after);
    }

    // Begin thua đua với SWEEPER: buổi vừa bị chốt SessionAbandoned giữa lúc đọc và UPDATE ⇒ 409
    // SESSION_ENDED và KHÔNG ghi BegunAt. Nhận diện "đã kết thúc" phải theo ĐỦ tập IsEnded, không chỉ
    // Scored (KIỂM R7).
    [Fact]
    public async Task Begin_ThuaDuaVoiSweeper_BuoiVuaAbandoned_409SessionEnded_KhongGhiBegunAt()
    {
        using var t = new TestDb();
        var c = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, c, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(1));
        var icp = new WinnerFirstInterceptor(async () =>
        {
            using var other = t.NewContext();
            await other.PracticeSessions.Where(x => x.Id == s.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, SessionStatus.SessionAbandoned));
        });
        using var db = t.NewContext(null, [icp]);

        var ex = await Assert.ThrowsAsync<SessionEndedException>(() => Practice(db).BeginSessionAsync(c, s.Id));
        Assert.True(icp.Fired);
        Assert.Equal("SESSION_ENDED", ex.Code);
        Assert.Null(Reload(t, s.Id).BegunAt);
    }

    // DB14 — ExecuteUpdate bỏ qua override SaveChanges ⇒ begin phải tự đóng dấu updated_at (KIỂM R8b).
    [Fact]
    public async Task Begin_DongDauUpdatedAt_Db14()
    {
        using var t = new TestDb();
        var c = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, c, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(1));
        await Task.Delay(20);
        var before = DateTime.UtcNow;

        await Practice(t.NewContext()).BeginSessionAsync(c, s.Id);

        var saved = Reload(t, s.Id);
        Assert.True(saved.UpdatedAt >= before, $"updated_at={saved.UpdatedAt:O} < before={before:O}");
        Assert.Equal(saved.BegunAt, saved.UpdatedAt);
    }

    // Người KHÁC gọi begin một buổi đã kết thúc ⇒ vẫn 403: kiểm chủ buổi TRƯỚC trạng thái, không lộ
    // trạng thái buổi của người khác qua 409 (KIỂM R10).
    [Fact]
    public async Task Begin_NguoiKhac_BuoiDaKetThuc_403_KhongLoTrangThai()
    {
        using var t = new TestDb();
        var (s, _, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(1),
            SessionStatus.Scored);

        var r = await PracticeCtl(t.NewContext(), Guid.NewGuid()).BeginSession(s.Id, default);

        var obj = Assert.IsAssignableFrom<ObjectResult>(r);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // ══ KIỂM B3 quan sát 3 — SessionDeadline:GraceSeconds đi qua DI tới CẢ HAI đầu ══════════════════
    //
    // Đăng ký y như Program.cs: Configure<SessionDeadlineOptions>(section) + AddScoped<IAnswerService,
    // AnswerService>() + hosted SessionAbandonSweeper (container DI chọn constructor có tham số optional).
    // Ân hạn 90 giây, hạn chót đã qua 60 giây: nếu MỘT trong hai đầu bỏ qua option (rơi về 30 mặc định)
    // thì upload bị 409 hoặc sweeper chốt buổi ⇒ test đỏ.
    [Fact]
    public async Task DI_SessionDeadlineGraceSeconds90_CaUploadLanSweeperDeuDocDung()
    {
        using var t = new TestDb();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SessionDeadline:GraceSeconds"] = "90"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        services.Configure<ScoringOptions>(_ => { });
        services.Configure<SessionDeadlineOptions>(config.GetSection(SessionDeadlineOptions.SectionName));
        var storage = new Mock<IStorageService>();
        storage.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/x.webm");
        services.AddSingleton(storage.Object);
        services.AddSingleton(new Mock<IScoringJobPublisher>().Object);
        services.AddSingleton(new Mock<ISessionScoringNotifier>().Object);
        services.AddSingleton(new Mock<IAiServiceQuestionGenerator>().Object);
        services.AddSingleton(new Mock<ICreditReservationClient>().Object);
        services.AddScoped<IPracticeService, PracticeService>();
        services.AddScoped<IAnswerService, AnswerService>();
        services.AddSingleton<SessionAbandonSweeper>();
        using var provider = services.BuildServiceProvider();

        var past60 = DateTime.UtcNow.AddSeconds(-60);
        var begun = DateTime.UtcNow.AddMinutes(-Duration);
        var (up, upQ1, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration, past60,
            SessionStatus.InProgress, begun);
        var (sw, _, _) = SeedSession(t, Guid.NewGuid(), Guid.NewGuid(), Duration, past60,
            SessionStatus.Ready, begun);

        using (var scope = provider.CreateScope())
        {
            var answers = scope.ServiceProvider.GetRequiredService<IAnswerService>();
            await answers.UploadAnswerAsync(up.Id, upQ1.Id, up.CandidateId,
                new MemoryStream(new byte[] { 1, 2, 3 }), "audio/webm", 20);
        }
        await ScanOnce(provider.GetRequiredService<SessionAbandonSweeper>());

        Assert.Single(t.NewContext().PracticeAnswers.Where(a => a.SessionId == up.Id));   // upload: 90 > 60 ⇒ nhận
        Assert.Equal(SessionStatus.Ready, Reload(t, sw.Id).Status);                        // sweeper: chưa chốt
    }

    // ══ Ví dụ JSON thật (in ra test output cho báo cáo) ════════════════════════════════════════

    [Fact]
    public async Task ViDuJson_GetTruocBegin_Begin_GetSauBegin()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (s, _, _) = SeedSession(t, candidate, Guid.NewGuid(), Duration, DateTime.UtcNow.AddDays(7));

        var get1 = (OkObjectResult)await PracticeCtl(t.NewContext(), candidate).GetSession(s.Id, default);
        var begin = (OkObjectResult)await PracticeCtl(t.NewContext(), candidate).BeginSession(s.Id, default);
        var get2 = (OkObjectResult)await PracticeCtl(t.NewContext(), candidate).GetSession(s.Id, default);

        var beginJson = Json(begin.Value);
        _out.WriteLine("GET trước begin: " + Json(get1.Value));
        _out.WriteLine("POST begin: " + beginJson);
        _out.WriteLine("GET sau begin: " + Json(get2.Value));

        using var doc = JsonDocument.Parse(beginJson);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "sessionId", "beganAt", "deadline", "serverNow", "durationMinutes" }, keys);
        Assert.EndsWith("Z", doc.RootElement.GetProperty("beganAt").GetString());
        Assert.EndsWith("Z", doc.RootElement.GetProperty("serverNow").GetString());

        using var g1 = JsonDocument.Parse(Json(get1.Value));
        foreach (var k in new[] { "durationMinutes", "beganAt", "serverNow", "questionsLocked" })
            Assert.True(g1.RootElement.TryGetProperty(k, out _), $"GET thiếu khoá {k}");
    }

    /// <summary>Chen tab "thắng" vào ĐÚNG khe giữa lúc đọc và câu UPDATE có điều kiện.</summary>
    private sealed class WinnerFirstInterceptor(Func<Task> winner) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired
                && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("begun_at", StringComparison.Ordinal))
            {
                Fired = true;
                await winner();
            }
            return result;
        }
    }
}
