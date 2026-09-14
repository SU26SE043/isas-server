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
}
