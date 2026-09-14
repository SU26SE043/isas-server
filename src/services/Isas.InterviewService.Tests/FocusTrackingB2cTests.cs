using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Ghi nhận mất tập trung cho buổi luyện B2C (coaching). KHÔNG phải chống gian lận:
/// người luyện tự bật, trình duyệt của họ tự báo, và chỉ họ đọc.
/// </summary>
public class FocusTrackingB2cTests
{
    // ── Nền dữ liệu ─────────────────────────────────────────────────────────

    [Fact]
    public void MacDinh_BuoiMoi_TatTheoDoiTapTrung()
    {
        // Mặc định phải là TẮT: mọi buổi đang chạy và client cũ giữ nguyên hành vi.
        var session = new PracticeSession();

        Assert.False(session.FocusTrackingEnabled);
    }

    [Fact]
    public async Task GhiDuocSuKien_KhiBuoiDaBatTheoDoi()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        session.FocusTrackingEnabled = true;
        t.Db.PracticeSessions.Add(session);
        await t.Db.SaveChangesAsync();

        t.Db.PracticeFocusEvents.Add(new PracticeFocusEvent
        {
            SessionId = session.Id,
            SignalType = FocusSignals.TabSwitch,
            Note = "rời tab",
            OccurredAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var saved = await t.Db.PracticeFocusEvents.SingleAsync(e => e.SessionId == session.Id);
        Assert.Equal(FocusSignals.TabSwitch, saved.SignalType);
        Assert.Equal("rời tab", saved.Note);
    }

    [Fact]
    public async Task XoaBuoi_XoaLuonSuKien()
    {
        // FK Cascade: xoá buổi là xoá sạch dấu vết, không để lại dòng mồ côi.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        session.FocusTrackingEnabled = true;
        t.Db.PracticeSessions.Add(session);
        await t.Db.SaveChangesAsync();
        t.Db.PracticeFocusEvents.Add(new PracticeFocusEvent
        {
            SessionId = session.Id, SignalType = FocusSignals.Paste, OccurredAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        t.Db.PracticeSessions.Remove(session);
        await t.Db.SaveChangesAsync();

        Assert.Empty(await t.Db.PracticeFocusEvents.ToListAsync());
    }

    [Fact]
    public async Task TinHieuNgoaiWhitelist_ViPhamCheckOTangDb()
    {
        // Guard C# ở service có thể bị ai đó gỡ; CHECK ở DB là lớp cuối. ⚠ SQLite KHÔNG ép
        // độ dài varchar (bài học S11: funded_by varchar(16) vs enum 19 ký tự — 1569 test xanh,
        // production hỏng), nhưng SQLite CÓ ép CHECK, nên phép kiểm này thật sự đo được.
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress);
        t.Db.PracticeSessions.Add(session);
        await t.Db.SaveChangesAsync();

        t.Db.PracticeFocusEvents.Add(new PracticeFocusEvent
        {
            SessionId = session.Id, SignalType = "face_mismatch", OccurredAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    [Fact]
    public void Whitelist_ChiCoBaTinHieuHanhVi()
    {
        // camera_blocked / monitoring_gap CỐ Ý không có: chúng chỉ có nghĩa khi giám sát webcam,
        // mà B2C đã chốt "chỉ cờ hành vi" ⇒ thêm vào là tạo cờ ma ngay từ ngày đầu.
        Assert.Equal(
            new[] { "focus_lost", "paste", "tab_switch" },
            FocusSignals.Allowed.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.False(FocusSignals.IsAllowed("camera_blocked"));
        Assert.False(FocusSignals.IsAllowed("monitoring_gap"));
        Assert.False(FocusSignals.IsAllowed(null));
    }

    // ── Ghim toggle lúc tạo buổi ────────────────────────────────────────────

    [Fact]
    public void RequestTaoBuoi_CoFieldBatTheoDoi_MacDinhNull()
    {
        // null = "client cũ không gửi" ⇒ ResolveFocusTracking trả false (tắt). Field đặt CUỐI +
        // có default nên mọi call site positional cũ (RoadmapLessonService, test) không phải sửa.
        var request = new CreatePracticeSessionRequest(
            CvId: null, JdId: null, JobCategory: JobCategory.BE);

        Assert.Null(request.FocusTrackingEnabled);
    }

    [Theory]
    [InlineData(null, false)]   // client cũ không gửi → tắt
    [InlineData(false, false)]  // từ chối tường minh → tắt
    [InlineData(true, true)]    // bật tường minh → bật
    public void ResolveFocusTracking_NullVaFalseDeuLaTat(bool? requested, bool expected)
    {
        Assert.Equal(expected, PracticeService.ResolveFocusTracking(requested));
    }

    [Fact]
    public void ChuKyStartLesson_NhanToggle_MacDinhTat()
    {
        // Bỏ sót đường roadmap là tính năng tắt câm ở đó mà không lỗi nào nổ (bài học
        // ScoringJob.Seniority: "bỏ sót MỘT trong hai đường publish… hỏng âm thầm").
        var start = typeof(IRoadmapLessonService)
            .GetMethod(nameof(IRoadmapLessonService.StartLessonAsync))!;
        var retry = typeof(IRoadmapLessonService)
            .GetMethod(nameof(IRoadmapLessonService.RetryLessonAsync))!;

        foreach (var method in new[] { start, retry })
        {
            var p = method.GetParameters()
                .SingleOrDefault(x => x.Name == "focusTrackingEnabled");
            Assert.NotNull(p);
            Assert.Equal(typeof(bool), p!.ParameterType);
            Assert.True(p.HasDefaultValue);
            Assert.Equal(false, p.DefaultValue);
        }
    }

    // ── Endpoint nhận tín hiệu ──────────────────────────────────────────────

    private static PracticeService FocusService(InterviewDbContext db)
        => PracticeServiceFactory.ForFocusTests(db);

    private static async Task<PracticeSession> SeedSessionAsync(
        TestDb t, Guid candidateId, bool tracking = true,
        SessionStatus status = SessionStatus.InProgress, Guid? campaignId = null)
    {
        var s = TestDb.Session(candidateId, status, campaignId: campaignId);
        s.FocusTrackingEnabled = tracking;
        t.Db.PracticeSessions.Add(s);
        await t.Db.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task GhiSuKien_KhiDungChuBuoiVaDaBat()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        await FocusService(t.Db).RecordFocusEventAsync(
            candidateId, s.Id, new RecordFocusEventRequest(FocusSignals.TabSwitch, "12s"), default);

        var saved = await t.Db.PracticeFocusEvents.SingleAsync();
        Assert.Equal(FocusSignals.TabSwitch, saved.SignalType);
        Assert.Equal(s.Id, saved.SessionId);
    }

    [Fact]
    public async Task BuoiCuaNguoiKhac_403_VaKhongGhiGi()
    {
        // sessionId đến từ ROUTE và đi thẳng vào bảng. Không kiểm chủ sở hữu thì bất kỳ ai cũng
        // bơm được sự kiện vào buổi người khác — đúng lỗ Q4 đã xảy ra trên prod ở nhánh B2B.
        using var t = new TestDb();
        var s = await SeedSessionAsync(t, Guid.NewGuid());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            FocusService(t.Db).RecordFocusEventAsync(
                Guid.NewGuid(), s.Id, new RecordFocusEventRequest(FocusSignals.Paste), default));

        Assert.Empty(await t.Db.PracticeFocusEvents.ToListAsync());
    }

    [Fact]
    public async Task BuoiKhongTonTai_KeyNotFound()
    {
        using var t = new TestDb();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            FocusService(t.Db).RecordFocusEventAsync(
                Guid.NewGuid(), Guid.NewGuid(), new RecordFocusEventRequest(FocusSignals.Paste), default));
    }

    [Fact]
    public async Task TinHieuLa_400_VaKhongGhiGi()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FocusService(t.Db).RecordFocusEventAsync(
                candidateId, s.Id, new RecordFocusEventRequest("face_mismatch"), default));

        Assert.Empty(await t.Db.PracticeFocusEvents.ToListAsync());
    }

    [Fact]
    public async Task BuoiTatTheoDoi_NoOp_KhongNem()
    {
        // Tắt = "không quan sát", không phải lỗi của client. Ném ở đây sẽ biến một lựa chọn hợp lệ
        // thành lỗi đỏ trong console của người luyện (mẫu RecordFlagAsync của B2B: no-op, vẫn 204).
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId, tracking: false);

        await FocusService(t.Db).RecordFocusEventAsync(
            candidateId, s.Id, new RecordFocusEventRequest(FocusSignals.TabSwitch), default);

        Assert.Empty(await t.Db.PracticeFocusEvents.ToListAsync());
    }

    [Fact]
    public async Task BuoiB2B_NoOp_DuCoBatCo()
    {
        // Buổi thi có đường giám sát riêng (CampaignService.session_flags) phục vụ HR. Cho ghi ở đây
        // nữa là tạo hai nguồn số cho cùng một buổi, không ai biết bên nào đúng.
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId, campaignId: Guid.NewGuid());

        await FocusService(t.Db).RecordFocusEventAsync(
            candidateId, s.Id, new RecordFocusEventRequest(FocusSignals.TabSwitch), default);

        Assert.Empty(await t.Db.PracticeFocusEvents.ToListAsync());
    }

    [Theory]
    [InlineData(SessionStatus.Scored)]
    [InlineData(SessionStatus.SessionAbandoned)]
    public async Task BuoiDaKetThuc_NoOp(SessionStatus status)
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId, status: status);

        await FocusService(t.Db).RecordFocusEventAsync(
            candidateId, s.Id, new RecordFocusEventRequest(FocusSignals.FocusLost), default);

        Assert.Empty(await t.Db.PracticeFocusEvents.ToListAsync());
    }

    [Fact]
    public async Task ChamTranMotBuoi_NoOp_KhongPhinhBang()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);
        t.Db.PracticeFocusEvents.AddRange(Enumerable.Range(0, FocusSignals.MaxEventsPerSession)
            .Select(_ => new PracticeFocusEvent
            {
                SessionId = s.Id, SignalType = FocusSignals.TabSwitch, OccurredAt = DateTime.UtcNow
            }));
        await t.Db.SaveChangesAsync();

        await FocusService(t.Db).RecordFocusEventAsync(
            candidateId, s.Id, new RecordFocusEventRequest(FocusSignals.Paste), default);

        Assert.Equal(
            FocusSignals.MaxEventsPerSession,
            await t.Db.PracticeFocusEvents.CountAsync(e => e.SessionId == s.Id));
    }

    [Fact]
    public async Task NoteQuaDai_BiCatChuKhongNem()
    {
        // Cột varchar(256). ⚠ SQLite KHÔNG ép độ dài varchar (bài học S11) nên nếu không cắt ở C#,
        // test sẽ xanh 100% còn Postgres nổ "value too long" ở đúng đường nóng.
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        await FocusService(t.Db).RecordFocusEventAsync(
            candidateId, s.Id,
            new RecordFocusEventRequest(FocusSignals.Paste, new string('x', 1000)), default);

        var saved = await t.Db.PracticeFocusEvents.SingleAsync();
        Assert.Equal(256, saved.Note!.Length);
    }
}
