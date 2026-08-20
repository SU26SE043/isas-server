using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// EV1 — khối cập nhật bằng chứng nhận CẢ tên tiêu chí, không chỉ GUID.
///
/// <para><b>LƯỚI AN TOÀN PHÒNG XA, không phải bản vá cho lỗi đã chứng minh.</b> Prod có dòng log
/// <c>targetCriterionId='Giao tiếp &amp; trình bày' (parse=False)</c>, nhưng probe gọi lại
/// <c>/decide-next</c> trên 20 ca THẬT (prompt chưa sửa gì) cho <b>20/20 GUID hợp lệ</b> — model
/// không gọi bằng tên khi nó có danh sách ID. Ca trả tên đến từ buổi có <b>0 dòng</b>
/// <c>session_criterion_evidence</c>: snapshot rỗng ⇒ prompt không có ID nào. ⚠ Và vì snapshot rỗng
/// nên lưới này KHÔNG cứu được chính ca đó — không có dòng nào để giải mã.</para>
///
/// <para><b>Nguyên nhân gốc là SC2</b>: 112/176 buổi adaptive không có snapshot, vì snapshot gieo từ
/// <c>targetable</c> mà rubric riêng BC16 nhận DEFAULT <c>ScoringScope = Always</c> ⇒ rỗng (tương
/// quan 94% trên 90 buổi). Vá ở <c>RubricLibraryService</c>, không phải ở đây.</para>
///
/// <para>Giữ nhánh này vì nó là guard by-construction đúng nếp repo (<c>ParseTargets</c> bỏ id lạ /
/// <c>verify_jd_quote</c> bỏ quote không đối chiếu được): nhận đầu vào rộng hơn nhưng chỉ ghi thứ
/// KIỂM ĐƯỢC — phủ ca image AIService lệch nhịp / model đổi hành vi sau này.</para>
/// </summary>
public class EvidenceNameResolveEv1Tests
{
    private sealed class LogRecorder : ILogger<AnswerService>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Infos { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            if (logLevel == LogLevel.Information) Infos.Add(formatter(state, exception));
        }
    }

    private static AnswerService Build(
        TestDb t, DecideNextResult decision, ILogger<AnswerService>? logger = null)
    {
        var publisher = new Mock<IScoringJobPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        var decider = new Mock<IAiServiceInterviewDecider>();
        decider.Setup(x => x.DecideNextAsync(
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decision);

        // KHÔNG truyền question generator ⇒ TU1 không bù câu ⇒ test này chỉ đo đúng một thứ: evidence.
        return new AnswerService(
            t.Db, storage.Object, publisher.Object,
            new Mock<ISessionScoringNotifier>().Object, TestDb.ScoringOpts(),
            logger ?? NullLogger<AnswerService>.Instance, decider.Object,
            Options.Create(new AdaptiveOptions()));
    }

    private static PracticeSession ChainSession(Guid candidate)
    {
        var s = TestDb.Session(candidate, SessionStatus.Ready);
        s.AdaptiveEnabled = true;
        s.MaxQuestions = 20;
        s.MaxFollowUps = 0;
        s.MaxDeepPerQuestion = 3;
        return s;
    }

    private static PracticeQuestion Seed(Guid sessionId)
        => new()
        {
            Id = Guid.NewGuid(), SessionId = sessionId, OrderNo = 1, Content = "Câu gốc",
            TimeLimitSec = 120, Kind = QuestionKind.Seed, Depth = 0
        };

    /// `session_criterion_evidence.criterion_id` là FK → `rubric_criteria` (Restrict), nên mọi dòng
    /// bằng chứng phải đi kèm một tiêu chí THẬT — đúng như snapshot được gieo lúc tạo buổi.
    private static SessionCriterionEvidence AddEvidence(TestDb t, PracticeSession session, string name)
    {
        var criterion = new RubricCriterion
        {
            Id = Guid.NewGuid(), Name = name, Weight = 0.2m, MaxScore = 5,
            IsActive = true, JobCategory = session.JobCategory, Language = session.Language,
            ScoringScope = ScoringScope.WhenTargeted, Version = 1
        };
        var evidence = new SessionCriterionEvidence
        {
            SessionId = session.Id,
            CriterionId = criterion.Id,
            CriterionName = name,
            State = "UNKNOWN"
        };
        t.Db.AddRange(criterion, evidence);
        return evidence;
    }

    private static async Task UploadAsync(AnswerService svc, Guid sessionId, Guid questionId, Guid candidate)
    {
        using var audio = new MemoryStream(new byte[] { 1 });
        await svc.UploadAnswerAsync(sessionId, questionId, candidate, audio, "audio/webm", 30);
    }

    private static DecideNextResult Decision(string targetCriterionId, string state = "PARTIAL")
        => new("end", null, "ts", null,
            TargetCriterionId: targetCriterionId,
            EvidenceFound: ["ứng viên nêu ví dụ thật"],
            MissingEvidence: ["chưa nói về đánh đổi"],
            NewEvidenceState: state);

    // ── Đường CŨ không đổi một chữ ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GuiGuid_VanDiDuongCu()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        var ev = AddEvidence(t, session, "Chiều sâu kỹ thuật");
        await t.Db.SaveChangesAsync();

        await UploadAsync(Build(t, Decision(ev.CriterionId.ToString())), session.Id, root.Id, candidate);

        var saved = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id);
        Assert.Equal("PARTIAL", saved.State);
        Assert.Equal(1, saved.DeepCount);
        Assert.Equal(["ứng viên nêu ví dụ thật"], saved.EvidenceFound);
    }

    /// GUID hợp lệ nhưng KHÔNG thuộc snapshot của buổi ⇒ vẫn bỏ qua + log (không rơi sang tìm theo tên).
    [Fact]
    public async Task GuiGuidLa_KhongThuocSnapshot_BoQua()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        AddEvidence(t, session, "Chiều sâu kỹ thuật");
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        await UploadAsync(
            Build(t, Decision(Guid.NewGuid().ToString()), log), session.Id, root.Id, candidate);

        var saved = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id);
        Assert.Equal("UNKNOWN", saved.State);
        Assert.Equal(0, saved.DeepCount);
        Assert.Contains(log.Warnings, w => w.Contains("KHÔNG thuộc snapshot"));
    }

    // ── Đường MỚI: giải mã theo TÊN ─────────────────────────────────────────────────────────────

    /// Hình dạng của dòng log prod (`targetCriterionId='Giao tiếp & trình bày'`) — nhưng ở đây buổi CÓ
    /// snapshot, tức đúng ca mà lưới an toàn này cứu được. Ca thật trên prod thì snapshot RỖNG (SC2),
    /// và khi đó không có dòng nào để giải mã — xem chú thích đầu file.
    [Fact]
    public async Task GuiTen_GiaiMaDungDong_VaCapNhat()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        var giaoTiep = AddEvidence(t, session, "Giao tiếp & trình bày");
        var chieuSau = AddEvidence(t, session, "Chiều sâu kỹ thuật");
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        await UploadAsync(
            Build(t, Decision("Giao tiếp & trình bày"), log), session.Id, root.Id, candidate);

        var hit = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.CriterionId == giaoTiep.CriterionId);
        var untouched = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.CriterionId == chieuSau.CriterionId);

        Assert.Equal("PARTIAL", hit.State);
        Assert.Equal(1, hit.DeepCount);
        Assert.Equal("UNKNOWN", untouched.State);          // KHÔNG đụng dòng khác
        Assert.Equal(0, untouched.DeepCount);
        Assert.Contains(log.Infos, i => i.Contains("giải mã") && i.Contains("theo TÊN"));
    }

    /// Chuẩn hoá NHẸ: trim · hoa/thường · khoảng trắng thừa. Hết — không bỏ dấu, không stem.
    [Fact]
    public async Task GuiTen_KhacHoaThuong_VaKhoangTrangThua_VanKhop()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        AddEvidence(t, session, "Thuật ngữ chuyên ngành");
        await t.Db.SaveChangesAsync();

        await UploadAsync(
            Build(t, Decision("  THUẬT ngữ   chuyên\tngành ")), session.Id, root.Id, candidate);

        var saved = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id);
        Assert.Equal("PARTIAL", saved.State);
        Assert.Equal(1, saved.DeepCount);
    }

    /// Tên GẦN GIỐNG (khác một chữ) KHÔNG được khớp: khớp nhầm ở đây là gán bằng chứng cho SAI tiêu
    /// chí — tệ hơn hẳn bỏ qua, vì nó ghi dữ liệu sai chứ không chỉ bỏ sót một lượt cập nhật.
    [Fact]
    public async Task GuiTenGanGiong_KhongKhop_BoQua()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        AddEvidence(t, session, "Chiều sâu kỹ thuật");
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        await UploadAsync(Build(t, Decision("Chiều sâu kỹ thuậ"), log), session.Id, root.Id, candidate);

        var saved = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id);
        Assert.Equal("UNKNOWN", saved.State);
        Assert.Equal(0, saved.DeepCount);
        Assert.Contains(log.Warnings, w => w.Contains("khớp theo tên=False"));
    }

    /// Tên hoàn toàn lạ ⇒ giữ nguyên hành vi cũ (bỏ qua + log), và log phải nói rõ đã thử CẢ HAI đường.
    [Fact]
    public async Task GuiTenLa_BoQua_LogNoiRoDaThuCaHaiDuong()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        AddEvidence(t, session, "Chiều sâu kỹ thuật");
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        await UploadAsync(Build(t, Decision("Một tiêu chí không tồn tại"), log), session.Id, root.Id, candidate);

        var saved = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id);
        Assert.Equal("UNKNOWN", saved.State);
        var warn = Assert.Single(log.Warnings, w => w.Contains("bỏ qua cập nhật cho answer"));
        Assert.Contains("parse=False", warn);
        Assert.Contains("khớp theo tên=False", warn);
    }

    /// Tên trùng NHIỀU dòng: không nên xảy ra (tên trong một rubric là duy nhất) nhưng rubric riêng
    /// BC16 do ứng viên tự CRUD nên không có gì bảo đảm ⇒ phải chịu được: bỏ qua + log, KHÔNG đoán.
    [Fact]
    public async Task TenTrungNhieuDong_BoQua_KhongDoan()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        AddEvidence(t, session, "Chiều sâu kỹ thuật");
        AddEvidence(t, session, "chiều sâu KỸ THUẬT");
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        await UploadAsync(Build(t, Decision("Chiều sâu kỹ thuật"), log), session.Id, root.Id, candidate);

        var rows = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .Where(e => e.SessionId == session.Id).ToListAsync();
        Assert.All(rows, r => Assert.Equal("UNKNOWN", r.State));
        Assert.All(rows, r => Assert.Equal(0, r.DeepCount));
        Assert.Contains(log.Warnings, w => w.Contains("khớp 2 tiêu chí") && w.Contains("không đoán"));
    }

    /// State KHÔNG hợp lệ ⇒ bỏ qua kể cả khi tên/GUID giải mã được: state mới là thứ được ghi.
    [Fact]
    public async Task StateKhongHopLe_BoQua_DuTenKhop()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        AddEvidence(t, session, "Chiều sâu kỹ thuật");
        await t.Db.SaveChangesAsync();

        var log = new LogRecorder();
        await UploadAsync(
            Build(t, Decision("Chiều sâu kỹ thuật", state: "MAYBE"), log), session.Id, root.Id, candidate);

        var saved = await t.Db.SessionCriterionEvidence.AsNoTracking()
            .SingleAsync(e => e.SessionId == session.Id);
        Assert.Equal("UNKNOWN", saved.State);
        Assert.Contains(log.Warnings, w => w.Contains("hợp lệ=False"));
    }
}
