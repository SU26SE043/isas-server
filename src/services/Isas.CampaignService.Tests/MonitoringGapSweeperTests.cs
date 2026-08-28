using System.Reflection;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Tests;

/// <summary>
/// MON1-B2 — <see cref="MonitoringGapSweeper"/>: phép đo độc lập phía server về khoảng trống giám sát
/// khuôn mặt GIỮA buổi thi (cặp ảnh Live liên tiếp cách nhau quá ngưỡng). Test khoá TỪNG điều kiện:
///  • campaign KHÔNG bật face_verify → 0 cờ (guard số một — nếu không sẽ gắn cờ 100% người vô tội);
///  • gap dưới ngưỡng → không cờ; gap trên ngưỡng → đúng 1 cờ (source=Server, note mô tả phép đo);
///  • quét 2 lần cùng dữ liệu → VẪN 1 cờ (chống trùng — session_flags không có UNIQUE);
///  • Enabled=false (chế độ bóng) → tính + trả GapsDetected nhưng KHÔNG ghi vào DB.
/// Gọi ScanOnceAsync qua reflection (idiom repo: FaceImagePurger/StuckScreeningRepublisher).
/// </summary>
public class MonitoringGapSweeperTests
{
    private static async Task<MonitoringGapSweeper.MonitoringGapScan> ScanOnce(MonitoringGapSweeper s)
    {
        var mi = typeof(MonitoringGapSweeper)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<MonitoringGapSweeper.MonitoringGapScan>)mi.Invoke(s, new object[] { CancellationToken.None })!;
    }

    private static MonitoringGapSweeper Build(CampaignTestDb t, MonitoringGapSettings? settings = null)
    {
        var provider = new ServiceCollection()
            .AddDbContext<CampaignDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention())
            .BuildServiceProvider();

        return new MonitoringGapSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings ?? new MonitoringGapSettings
            {
                Enabled = true,
                GapThresholdSeconds = 90,
                LookbackHours = 48
            }),
            NullLogger<MonitoringGapSweeper>.Instance);
    }

    // Campaign phải tồn tại trong DB: session_flags có FK bắt buộc tới campaigns (DB9).
    private static Campaign SeedCampaign(CampaignTestDb t, bool faceVerify)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.FaceVerifyEnabled = faceVerify;
        t.Db.Campaigns.Add(camp);
        t.Db.SaveChanges();
        return camp;
    }

    private static void SeedLiveShot(
        CampaignTestDb t, Guid campaignId, Guid sessionId, Guid candidateId, DateTime capturedAt)
    {
        t.Db.FaceImages.Add(new FaceImage
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            SessionId = sessionId,
            Kind = FaceImageKind.Live,
            StorageKey = $"campaigns/{campaignId:N}/sessions/{sessionId:N}/face-live-{Guid.NewGuid():N}.jpg",
            CapturedAt = capturedAt
        });
        t.Db.SaveChanges();
    }

    // ── GUARD SỐ MỘT: session của campaign KHÔNG bật face_verify → 0 cờ ─────────────────────────
    // Có sẵn 1 campaign face_verify KHÁC (để fveCampaignIds không rỗng ⇒ KHÔNG đi nhánh early-return)
    // ⇒ test này thực sự đo bộ lọc `fveCampaignIds.Contains(...)`, không phải chỉ cái short-circuit.
    [Fact]
    public async Task FaceVerifyTat_KhongCoCoNao()
    {
        using var t = new CampaignTestDb();
        var campTat = SeedCampaign(t, faceVerify: false);
        var campBat = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        // Buổi thi thuộc campaign ĐÃ TẮT face_verify, có gap 5' (thừa sức vượt ngưỡng).
        SeedLiveShot(t, campTat.Id, sid, cid, t0);
        SeedLiveShot(t, campTat.Id, sid, cid, t0.AddSeconds(300));
        // campaign face_verify khác: 1 ảnh, không tạo gap — chỉ để fveCampaignIds ≠ rỗng.
        SeedLiveShot(t, campBat.Id, Guid.NewGuid(), Guid.NewGuid(), t0);

        var r = await ScanOnce(Build(t));

        Assert.Equal(0, r.GapsDetected);
        Assert.Equal(0, r.FlagsWritten);
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── gap DƯỚI ngưỡng → không cờ ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task GapDuoiNguong_KhongCo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        SeedLiveShot(t, camp.Id, sid, cid, t0);
        SeedLiveShot(t, camp.Id, sid, cid, t0.AddSeconds(60));    // 60s < ngưỡng 90s

        var r = await ScanOnce(Build(t));

        Assert.Equal(0, r.GapsDetected);
        Assert.Equal(0, r.FlagsWritten);
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── gap TRÊN ngưỡng → đúng 1 cờ, đủ trường ──────────────────────────────────────────────────
    [Fact]
    public async Task GapTrenNguong_DungMotCo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-15);
        SeedLiveShot(t, camp.Id, sid, cid, t0);
        SeedLiveShot(t, camp.Id, sid, cid, t0.AddSeconds(300));

        var r = await ScanOnce(Build(t));

        Assert.Equal(1, r.GapsDetected);
        Assert.Equal(1, r.FlagsWritten);

        using var db = t.NewContext();
        var flag = await db.SessionFlags.SingleAsync();
        Assert.Equal("monitoring_gap", flag.SignalType);
        Assert.Equal(FlagSource.Server, flag.Source);
        Assert.Equal(sid, flag.SessionId);
        Assert.Equal(camp.Id, flag.CampaignId);
        Assert.Equal(cid, flag.CandidateId);
        Assert.NotNull(flag.Note);
        Assert.Contains("5 phút", flag.Note!);
        Assert.Contains("nhịp bình thường 30 giây", flag.Note!);
        Assert.Contains($"[gap#{t0.Ticks}]", flag.Note!);         // marker ổn định = mốc bắt đầu gap
        // CAMP-12: mô tả phép đo, KHÔNG phán xét.
        Assert.DoesNotContain("gian lận", flag.Note!);
        Assert.DoesNotContain("rời đi", flag.Note!);
    }

    // ── chống trùng: quét 2 lần cùng dữ liệu → VẪN 1 cờ ─────────────────────────────────────────
    [Fact]
    public async Task ChayHaiLan_VanMotCo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-20);
        SeedLiveShot(t, camp.Id, sid, cid, t0);
        SeedLiveShot(t, camp.Id, sid, cid, t0.AddSeconds(300));

        var first = await ScanOnce(Build(t));
        var second = await ScanOnce(Build(t));

        Assert.Equal(1, first.FlagsWritten);
        Assert.Equal(1, second.GapsDetected);   // vẫn phát hiện
        Assert.Equal(0, second.FlagsWritten);   // nhưng KHÔNG ghi thêm
        Assert.Equal(1, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── chế độ bóng: Enabled=false → tính + trả GapsDetected nhưng 0 row vào DB ──────────────────
    [Fact]
    public async Task EnabledFalse_KhongGhiVaoDB()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-15);
        SeedLiveShot(t, camp.Id, sid, cid, t0);
        SeedLiveShot(t, camp.Id, sid, cid, t0.AddSeconds(300));

        var r = await ScanOnce(Build(t, new MonitoringGapSettings
        {
            Enabled = false,
            GapThresholdSeconds = 90,
            LookbackHours = 48
        }));

        Assert.Equal(1, r.GapsDetected);   // đã TÍNH
        Assert.Equal(0, r.FlagsWritten);   // nhưng KHÔNG ghi
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── nhiều gap trong 1 buổi → nhiều cờ, marker RIÊNG (khoá mutation "marker hằng số") ────────
    [Fact]
    public async Task NhieuGap_NhieuCo_MarkerRieng()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        SeedLiveShot(t, camp.Id, sid, cid, t0);
        SeedLiveShot(t, camp.Id, sid, cid, t0.AddSeconds(300));   // gap #1: 300s
        SeedLiveShot(t, camp.Id, sid, cid, t0.AddSeconds(330));   // nhịp bình thường
        SeedLiveShot(t, camp.Id, sid, cid, t0.AddSeconds(600));   // gap #2: 270s

        var r = await ScanOnce(Build(t));

        Assert.Equal(2, r.GapsDetected);
        Assert.Equal(2, r.FlagsWritten);

        using var db = t.NewContext();
        var notes = await db.SessionFlags.Select(f => f.Note!).ToListAsync();
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Contains($"[gap#{t0.Ticks}]"));
        Assert.Contains(notes, n => n.Contains($"[gap#{t0.AddSeconds(330).Ticks}]"));

        // Quét lại → không nhân bản (dedup theo từng marker).
        var again = await ScanOnce(Build(t));
        Assert.Equal(0, again.FlagsWritten);
        Assert.Equal(2, await db.SessionFlags.CountAsync());
    }

    // ── cờ 'monitoring_gap' do CLIENT báo KHÔNG chặn cờ Server (dedup chỉ xét source=Server) ────
    [Fact]
    public async Task CoClientMonitoringGap_KhongChanCoServer()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-15);
        SeedLiveShot(t, camp.Id, sid, cid, t0);
        SeedLiveShot(t, camp.Id, sid, cid, t0.AddSeconds(300));

        // Cờ client sẵn có, note CHỨA marker của gap này — vẫn không được coi là "đã có".
        t.Db.SessionFlags.Add(new SessionFlag
        {
            Id = Guid.NewGuid(),
            SessionId = sid,
            CampaignId = camp.Id,
            CandidateId = cid,
            SignalType = "monitoring_gap",
            Source = FlagSource.Client,
            Note = $"tab ngủ [gap#{t0.Ticks}]",
            DetectedAt = DateTime.UtcNow
        });
        t.Db.SaveChanges();

        var r = await ScanOnce(Build(t));

        Assert.Equal(1, r.FlagsWritten);
        using var db = t.NewContext();
        Assert.Equal(1, await db.SessionFlags.CountAsync(f => f.Source == FlagSource.Server));
        Assert.Equal(1, await db.SessionFlags.CountAsync(f => f.Source == FlagSource.Client));
    }

    // ── ảnh ngoài cửa sổ LookbackHours → không xét ─────────────────────────────────────────────
    [Fact]
    public async Task GapNgoaiLookback_KhongXet()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddHours(-50);   // trước cửa sổ 48h
        SeedLiveShot(t, camp.Id, sid, cid, t0);
        SeedLiveShot(t, camp.Id, sid, cid, t0.AddSeconds(300));

        var r = await ScanOnce(Build(t));

        Assert.Equal(0, r.GapsDetected);
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── mặc định: chế độ bóng, ngưỡng 90, quét 120, nhìn lại 48 (TEST-09: default chưa test = trôi) ─
    [Fact]
    public void MacDinh_CheDoBong_Nguong90_Lookback48()
    {
        var d = new MonitoringGapSettings();
        Assert.False(d.Enabled);
        Assert.Equal(90, d.GapThresholdSeconds);
        Assert.Equal(120, d.ScanIntervalSeconds);
        Assert.Equal(48, d.LookbackHours);
        Assert.Equal(120, d.MinDurationSeconds);   // MON1-B3
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  MON1-B3 — LUẬT 2: KHÔNG có lượt kiểm nào trong suốt buổi thi
    //  Lấp đòn B2 (cần 2 điểm để so) không bắt được: chặn endpoint giây đầu ⇒ 0 ảnh.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    private static void SeedTerminalMembership(
        CampaignTestDb t, Guid campaignId, Guid sessionId, Guid candidateId,
        DateTime startedAt, DateTime updatedAt,
        InterviewProgressStatus status = InterviewProgressStatus.Completed)
    {
        var m = CampaignTestDb.NewMembership(campaignId, candidateId, sessionId: sessionId, interviewStatus: status);
        m.InterviewStartedAt = startedAt;
        m.UpdatedAt = updatedAt;
        t.Db.CampaignMemberships.Add(m);
        t.Db.SaveChanges();
    }

    // ── buổi CÓ ảnh (≥1 Live) → KHÔNG cờ B3 (hasShot). 1 ảnh ⇒ cũng không có gap B2. ─────────────
    [Fact]
    public async Task NoShot_BuoiCoAnh_KhongCo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-20);
        SeedTerminalMembership(t, camp.Id, sid, cid, start, start.AddMinutes(15));
        SeedLiveShot(t, camp.Id, sid, cid, start.AddMinutes(1));   // có đúng 1 lượt kiểm

        var r = await ScanOnce(Build(t));

        Assert.Equal(0, r.NoShotSessions);
        Assert.Equal(0, r.FlagsWritten);
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── buổi 0 ảnh nhưng NGẮN hơn ngưỡng → KHÔNG cờ ───────────────────────────────────────────────
    [Fact]
    public async Task NoShot_BuoiNganHonNguong_KhongCo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-5);
        SeedTerminalMembership(t, camp.Id, sid, cid, start, start.AddSeconds(60));   // 60s < 120s

        var r = await ScanOnce(Build(t));

        Assert.Equal(0, r.NoShotSessions);
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── buổi 0 ảnh, ĐỦ DÀI → đúng 1 cờ, đủ trường ────────────────────────────────────────────────
    [Fact]
    public async Task NoShot_BuoiDuDai_DungMotCo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-25);
        SeedTerminalMembership(t, camp.Id, sid, cid, start, start.AddMinutes(10));   // 10' > 120s

        var r = await ScanOnce(Build(t));

        Assert.Equal(1, r.NoShotSessions);
        Assert.Equal(1, r.FlagsWritten);

        using var db = t.NewContext();
        var flag = await db.SessionFlags.SingleAsync();
        Assert.Equal("monitoring_gap", flag.SignalType);
        Assert.Equal(FlagSource.Server, flag.Source);
        Assert.Equal(sid, flag.SessionId);
        Assert.Equal(camp.Id, flag.CampaignId);
        Assert.Equal(cid, flag.CandidateId);
        Assert.NotNull(flag.Note);
        Assert.Contains("trong suốt buổi thi", flag.Note!);
        Assert.Contains("(10 phút)", flag.Note!);
        Assert.Contains("[monitor#none]", flag.Note!);
        Assert.DoesNotContain("gian lận", flag.Note!);
        Assert.DoesNotContain("rời đi", flag.Note!);
    }

    // ── chống trùng: quét 2 lần → vẫn 1 cờ (mỗi session tối đa 1 cờ loại này) ─────────────────────
    [Fact]
    public async Task NoShot_ChayHaiLan_VanMotCo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-25);
        SeedTerminalMembership(t, camp.Id, sid, cid, start, start.AddMinutes(10));

        var first = await ScanOnce(Build(t));
        var second = await ScanOnce(Build(t));

        Assert.Equal(1, first.FlagsWritten);
        Assert.Equal(1, second.NoShotSessions);   // vẫn phát hiện
        Assert.Equal(0, second.FlagsWritten);     // nhưng KHÔNG ghi thêm
        Assert.Equal(1, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── campaign KHÔNG bật face_verify → KHÔNG cờ (có 1 fve campaign khác để không đi early-return) ─
    [Fact]
    public async Task NoShot_FaceVerifyTat_KhongCo()
    {
        using var t = new CampaignTestDb();
        var campTat = SeedCampaign(t, faceVerify: false);
        var campBat = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-25);
        SeedTerminalMembership(t, campTat.Id, sid, cid, start, start.AddMinutes(10));
        // fve campaign khác: 1 membership terminal CÓ ảnh — chỉ để fveCampaignIds ≠ rỗng.
        var sid2 = Guid.NewGuid();
        SeedTerminalMembership(t, campBat.Id, sid2, Guid.NewGuid(), start, start.AddMinutes(10));
        SeedLiveShot(t, campBat.Id, sid2, Guid.NewGuid(), start.AddMinutes(1));

        var r = await ScanOnce(Build(t));

        Assert.Equal(0, r.NoShotSessions);
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── buổi CHƯA terminal (InProgress) → KHÔNG cờ. Seed StartedAt cũ + UpdatedAt mới (duration dài)
    //    để CÔ LẬP điều kiện terminal khỏi ngưỡng thời lượng: nếu chỉ ngưỡng chặn thì mutation "bỏ
    //    điều kiện terminal" sẽ không ĐỎ. ───────────────────────────────────────────────────────────
    [Fact]
    public async Task NoShot_ChuaTerminal_KhongCo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-15);
        SeedTerminalMembership(t, camp.Id, sid, cid, start, DateTime.UtcNow,
            status: InterviewProgressStatus.InProgress);   // duration ~15' nhưng CHƯA terminal

        var r = await ScanOnce(Build(t));

        Assert.Equal(0, r.NoShotSessions);
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── chế độ bóng: Enabled=false → NoShotSessions>0 nhưng 0 row vào DB ─────────────────────────
    [Fact]
    public async Task NoShot_EnabledFalse_KhongGhiVaoDB()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-25);
        SeedTerminalMembership(t, camp.Id, sid, cid, start, start.AddMinutes(10));

        var r = await ScanOnce(Build(t, new MonitoringGapSettings
        {
            Enabled = false,
            GapThresholdSeconds = 90,
            MinDurationSeconds = 120,
            LookbackHours = 48
        }));

        Assert.Equal(1, r.NoShotSessions);   // đã TÍNH
        Assert.Equal(0, r.FlagsWritten);     // nhưng KHÔNG ghi
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }

    // ── buổi terminal kết thúc quá lâu (ngoài LookbackHours) → KHÔNG xét ─────────────────────────
    [Fact]
    public async Task NoShot_NgoaiLookback_KhongXet()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t, faceVerify: true);
        var sid = Guid.NewGuid();
        var cid = Guid.NewGuid();
        var start = DateTime.UtcNow.AddHours(-51);
        SeedTerminalMembership(t, camp.Id, sid, cid, start, start.AddMinutes(10));   // UpdatedAt ~ now-50h

        var r = await ScanOnce(Build(t));

        Assert.Equal(0, r.NoShotSessions);
        Assert.Equal(0, await t.NewContext().SessionFlags.CountAsync());
    }
}
