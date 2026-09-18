using System.Text;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// B2C coaching (2026-09-17) — kiểm mặt định kỳ (đếm mặt, detect-only). KHÔNG so khớp danh tính,
/// không HR/admin đọc được. Guard theo ĐÚNG thứ tự <see cref="PracticeService.RecordFocusEventAsync"/>.
/// </summary>
public class PracticeFaceCheckB2cTests
{
    // JPEG SOI (FF D8 FF) + đủ dài để khớp trần "quá lớn" khi cần.
    private static readonly byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };

    private static Stream Jpeg() => new MemoryStream(JpegBytes);

    private static PracticeFaceCheckService Service(
        InterviewDbContext db, Mock<IStorageService>? storage = null,
        Mock<IAiServiceFaceDetector>? detector = null)
    {
        storage ??= new Mock<IStorageService>();
        detector ??= new Mock<IAiServiceFaceDetector>();
        return new PracticeFaceCheckService(
            db, storage.Object, detector.Object, NullLogger<PracticeFaceCheckService>.Instance);
    }

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

    // ── CHECK DB nới đủ 5 giá trị ────────────────────────────────────────────

    [Fact]
    public async Task Persistable_KhopCheckDb_ChenDuocCaHaiLoaiMat()
    {
        using var t = new TestDb();
        var s = await SeedSessionAsync(t, Guid.NewGuid());

        t.Db.PracticeFocusEvents.AddRange(
            new PracticeFocusEvent { SessionId = s.Id, SignalType = FocusSignals.NoFace, OccurredAt = DateTime.UtcNow },
            new PracticeFocusEvent { SessionId = s.Id, SignalType = FocusSignals.MultipleFaces, OccurredAt = DateTime.UtcNow });

        await t.Db.SaveChangesAsync();   // không ném — CHECK đã nới

        Assert.Equal(2, await t.Db.PracticeFocusEvents.CountAsync(e => e.SessionId == s.Id));
    }

    [Fact]
    public async Task TinHieuNgoaiCaWhitelist_ViPhamCheckOTangDb()
    {
        using var t = new TestDb();
        var s = await SeedSessionAsync(t, Guid.NewGuid());
        t.Db.PracticeFocusEvents.Add(new PracticeFocusEvent
        {
            SessionId = s.Id, SignalType = "face_mismatch", OccurredAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    // ── Ghi SỔ trước rồi mới upload ──────────────────────────────────────────

    [Fact]
    public async Task GhiSoTruocKhiUpload_UploadNemVanConDongSo()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        var storage = new Mock<IStorageService>();
        storage.Setup(x => x.UploadObjectAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("S3 lỗi"));

        var svc = Service(t.Db, storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default));

        Assert.Single(await t.Db.PracticeFaceImages.ToListAsync());
    }

    // ── Kết quả detect ────────────────────────────────────────────────────────

    [Fact]
    public async Task KhongThayMat_GhiNoFace_KemNote_Tra200()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        var detector = new Mock<IAiServiceFaceDetector>();
        detector.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceDetectResult(0, new List<string> { "no_face" }));

        var svc = Service(t.Db, detector: detector);
        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.NotNull(res);
        Assert.Equal(0, res!.FaceCount);
        Assert.Equal(new[] { "no_face" }, res.Signals);

        var saved = await t.Db.PracticeFocusEvents.SingleAsync();
        Assert.Equal(FocusSignals.NoFace, saved.SignalType);
        Assert.Equal("Không thấy bạn trong khung hình", saved.Note);
    }

    [Fact]
    public async Task NhieuMat_GhiMultipleFaces_KemSoNguoi()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        var detector = new Mock<IAiServiceFaceDetector>();
        detector.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceDetectResult(3, new List<string> { "multiple_faces" }));

        var svc = Service(t.Db, detector: detector);
        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.Equal(3, res!.FaceCount);
        var saved = await t.Db.PracticeFocusEvents.SingleAsync();
        Assert.Equal(FocusSignals.MultipleFaces, saved.SignalType);
        Assert.Equal("Có 3 người trong khung hình", saved.Note);
    }

    [Fact]
    public async Task MotMat_KhongGhiFocusEvent_TraSignalsRong()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        var detector = new Mock<IAiServiceFaceDetector>();
        detector.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceDetectResult(1, new List<string>()));

        var svc = Service(t.Db, detector: detector);
        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.Equal(1, res!.FaceCount);
        Assert.Empty(res.Signals);
        Assert.Empty(await t.Db.PracticeFocusEvents.ToListAsync());
    }

    [Fact]
    public async Task TinHieuLa_BiLocKhoiKetQuaVaKhongGhi()
    {
        // AIService hỏng/trả sai (hoặc client nhái) không được lọt vào bảng dành riêng cho
        // no_face/multiple_faces.
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        var detector = new Mock<IAiServiceFaceDetector>();
        detector.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceDetectResult(1, new List<string> { "face_mismatch", "no_face" }));

        var svc = Service(t.Db, detector: detector);
        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.Equal(new[] { "no_face" }, res!.Signals);
        var saved = Assert.Single(await t.Db.PracticeFocusEvents.ToListAsync());
        Assert.Equal(FocusSignals.NoFace, saved.SignalType);
    }

    // ── No-op — không upload, không gọi AI ──────────────────────────────────

    [Fact]
    public async Task BuoiTatTheoDoi_NoOp_KhongUploadKhongGoiAi()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId, tracking: false);

        var storage = new Mock<IStorageService>();
        var detector = new Mock<IAiServiceFaceDetector>();
        var svc = Service(t.Db, storage, detector);

        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.Null(res);
        storage.Verify(x => x.UploadObjectAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        detector.Verify(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await t.Db.PracticeFaceImages.ToListAsync());
    }

    [Fact]
    public async Task BuoiB2B_NoOp_KhongUploadKhongGoiAi()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId, campaignId: Guid.NewGuid());

        var storage = new Mock<IStorageService>();
        var detector = new Mock<IAiServiceFaceDetector>();
        var svc = Service(t.Db, storage, detector);

        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.Null(res);
        storage.Verify(x => x.UploadObjectAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(SessionStatus.Scored)]
    [InlineData(SessionStatus.SessionAbandoned)]
    [InlineData(SessionStatus.Scoring)]
    [InlineData(SessionStatus.Completed)]
    [InlineData(SessionStatus.Failed)]
    public async Task DaKetThuc_NoOp(SessionStatus status)
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId, status: status);

        var svc = Service(t.Db);
        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.Null(res);
    }

    // ── Chủ sở hữu / tồn tại ─────────────────────────────────────────────────

    [Fact]
    public async Task NguoiKhac_403_KhongGhiSo()
    {
        using var t = new TestDb();
        var s = await SeedSessionAsync(t, Guid.NewGuid());
        var svc = Service(t.Db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.RecordFaceCheckAsync(Guid.NewGuid(), s.Id, Jpeg(), JpegBytes.Length, default));

        Assert.Empty(await t.Db.PracticeFaceImages.ToListAsync());
    }

    [Fact]
    public async Task KhongTonTai_404()
    {
        using var t = new TestDb();
        var svc = Service(t.Db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.RecordFaceCheckAsync(Guid.NewGuid(), Guid.NewGuid(), Jpeg(), JpegBytes.Length, default));
    }

    // ── Lỗi AI ném ra (sổ + ảnh đã ghi) ──────────────────────────────────────

    [Fact]
    public async Task AiLoi_NemRaNgoai_NhungSoVaAnhDaGhi()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        var detector = new Mock<IAiServiceFaceDetector>();
        detector.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /face-detect trả 502"));

        var svc = Service(t.Db, detector: detector);

        await Assert.ThrowsAsync<AiServiceException>(() =>
            svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default));

        // AI chưa trả lời ⇒ ảnh chưa xong việc ⇒ KHÔNG xoá, dòng sổ giữ nguyên cho purger dọn về sau.
        Assert.Single(await t.Db.PracticeFaceImages.ToListAsync());
    }

    // ── Ảnh là vật trung gian: detect xong là xoá (S3 TRƯỚC, dòng sổ SAU) ───────

    [Fact]
    public async Task DetectXong_XoaS3Truoc_RoiMoiGoDongSo_KhongDeAnhLai()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        var detector = new Mock<IAiServiceFaceDetector>();
        detector.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceDetectResult(1, new List<string>()));

        string? uploadedKey = null;
        var rowsWhenS3Deleted = -1;
        var storage = new Mock<IStorageService>();
        storage.Setup(x => x.UploadObjectAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, CancellationToken>((k, _, _, _) => uploadedKey = k)
            .Returns(Task.CompletedTask);
        storage.Setup(x => x.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            // Đo THỨ TỰ: lúc S3 bị xoá, dòng sổ PHẢI còn (xoá dòng trước = object mồ côi nếu chết ở đây).
            .Callback<string, CancellationToken>((_, _) =>
                rowsWhenS3Deleted = t.Db.PracticeFaceImages.AsNoTracking().Count())
            .Returns(Task.CompletedTask);

        var svc = Service(t.Db, storage, detector);
        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.NotNull(res);
        Assert.NotNull(uploadedKey);
        storage.Verify(x => x.DeleteObjectAsync(uploadedKey!, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, rowsWhenS3Deleted);                                   // S3 trước
        Assert.Empty(await t.Db.PracticeFaceImages.ToListAsync());            // dòng sổ sau
    }

    [Fact]
    public async Task XoaS3Loi_GiuDongSoChoPurger_VanGhiCoVaTraKetQua()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);

        var detector = new Mock<IAiServiceFaceDetector>();
        detector.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceDetectResult(0, new List<string> { "no_face" }));

        var storage = new Mock<IStorageService>();
        storage.Setup(x => x.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SeaweedFS down"));

        var svc = Service(t.Db, storage, detector);
        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.NotNull(res);                                                  // xoá hụt KHÔNG làm hỏng lượt
        Assert.Equal(new[] { "no_face" }, res!.Signals);
        Assert.Single(await t.Db.PracticeFocusEvents.ToListAsync());         // cờ vẫn ghi
        Assert.Single(await t.Db.PracticeFaceImages.ToListAsync());          // dòng sổ giữ → purger thử lại
    }

    // ── Trần dòng dùng chung với FocusSignals.MaxEventsPerSession ───────────

    [Fact]
    public async Task ChamTran500_KhongGhiFocusEvent_AnhVanDuocDonSauDetect()
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

        var detector = new Mock<IAiServiceFaceDetector>();
        detector.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceDetectResult(0, new List<string> { "no_face" }));

        var svc = Service(t.Db, detector: detector);
        var res = await svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), JpegBytes.Length, default);

        Assert.NotNull(res);   // vẫn trả kết quả cho người luyện đọc
        Assert.Equal(
            FocusSignals.MaxEventsPerSession,
            await t.Db.PracticeFocusEvents.CountAsync(e => e.SessionId == s.Id));   // không phình thêm
        Assert.Empty(await t.Db.PracticeFaceImages.ToListAsync());    // detect xong ⇒ ảnh + dòng sổ đã dọn
    }

    // ── Cổng định dạng/độ lớn ────────────────────────────────────────────────

    [Fact]
    public async Task AnhRong_400()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);
        var svc = Service(t.Db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RecordFaceCheckAsync(candidateId, s.Id, new MemoryStream(), 0, default));
    }

    [Fact]
    public async Task AnhQuaLon_400()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);
        var svc = Service(t.Db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RecordFaceCheckAsync(candidateId, s.Id, Jpeg(), 2 * 1024 * 1024 + 1, default));
    }

    [Fact]
    public async Task KhongPhaiJpeg_400()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);
        var svc = Service(t.Db);

        var notJpeg = new MemoryStream(Encoding.UTF8.GetBytes("khong-phai-anh"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RecordFaceCheckAsync(candidateId, s.Id, notJpeg, notJpeg.Length, default));

        Assert.Empty(await t.Db.PracticeFaceImages.ToListAsync());
    }

    // ── GetSessionAsync gom cả khung hình vào tổng hợp (không cần sửa code) ──

    [Fact]
    public async Task GetSession_TongHopGomCaTinHieuMat()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var s = await SeedSessionAsync(t, candidateId);
        t.Db.PracticeFocusEvents.AddRange(
            new PracticeFocusEvent { SessionId = s.Id, SignalType = FocusSignals.TabSwitch, OccurredAt = DateTime.UtcNow },
            new PracticeFocusEvent { SessionId = s.Id, SignalType = FocusSignals.NoFace, OccurredAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();

        var response = await PracticeServiceFactory.ForFocusTests(t.Db).GetSessionAsync(candidateId, s.Id, default);

        Assert.True(response!.FocusTrackingEnabled);
        Assert.Contains(response.FocusEvents!, e => e.SignalType == FocusSignals.TabSwitch);
        Assert.Contains(response.FocusEvents!, e => e.SignalType == FocusSignals.NoFace);
    }
}
