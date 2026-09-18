using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// B2C coaching (2026-09-17, BC-6 ngoại lệ) — ghi 1 lượt kiểm mặt: ghi sổ + upload ảnh S3 → hỏi
/// AIService `/face-detect` (đếm mặt, detect-only) → gắn cờ <c>no_face</c>/<c>multiple_faces</c>
/// cho chính người luyện đọc lại. KHÔNG so khớp danh tính, KHÔNG HR/admin đọc được.
///
/// Ảnh là VẬT TRUNG GIAN: nó chỉ tồn tại để AIService đọc được qua S3 key. Detect xong là xoá
/// ngay (S3 TRƯỚC, dòng sổ SAU — bất biến <see cref="PracticeFaceImage"/>). Không có màn nào đọc
/// lại ảnh (khác B2B, nơi HR cần bằng chứng — BK25), nên giữ lại là tích dữ liệu sinh trắc học
/// (DATA-3) không mục đích: nhịp 15s × buổi 30' ≈ 120 ảnh/buổi. Sổ + <see cref="PracticeFaceImagePurger"/>
/// chỉ còn là LƯỚI AN TOÀN cho phần sót (AI lỗi/hết giờ, chết giữa chừng, xoá S3 hụt).
///
/// Guard theo ĐÚNG thứ tự <see cref="PracticeService.RecordFocusEventAsync"/> để hai đường ghi
/// mất-tập-trung (hành vi + mặt) không lệch nhau: không tồn tại → không phải chủ (TRƯỚC mọi no-op,
/// bịt lỗ Q4 — ai đó bơm sự kiện vào buổi người khác) → B2B/tắt/đã kết thúc → no-op (null).
/// </summary>
public class PracticeFaceCheckService : IPracticeFaceCheckService
{
    private const long MaxImageBytes = 2 * 1024 * 1024;   // 2MB

    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceFaceDetector _detector;
    private readonly ILogger<PracticeFaceCheckService> _logger;

    public PracticeFaceCheckService(
        InterviewDbContext db, IStorageService storage, IAiServiceFaceDetector detector,
        ILogger<PracticeFaceCheckService> logger)
    {
        _db = db;
        _storage = storage;
        _detector = detector;
        _logger = logger;
    }

    public async Task<FaceCheckResultResponse?> RecordFaceCheckAsync(
        Guid candidateId, Guid sessionId, Stream image, long length, CancellationToken ct = default)
    {
        var session = await _db.PracticeSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new KeyNotFoundException("Session không tồn tại");

        if (session.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải buổi của bạn");

        // Buổi B2B có đường giám sát riêng (CampaignService.session_flags) phục vụ HR.
        if (session.CampaignId is not null) return null;

        // Người luyện không bật ⇒ không quan sát. No-op — KHÔNG upload, KHÔNG gọi AI (tốn tiền vô ích).
        if (!session.FocusTrackingEnabled) return null;

        // Buổi đã đóng sổ ⇒ số liệu coaching đã chốt.
        if (session.Status is SessionStatus.Scored or SessionStatus.SessionAbandoned
            or SessionStatus.Scoring or SessionStatus.Completed or SessionStatus.Failed) return null;

        if (length <= 0 || length > MaxImageBytes)
            throw new InvalidOperationException("Ảnh trống hoặc vượt quá 2MB.");

        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var head = new byte[3];
        var read = await buffer.ReadAsync(head, 0, head.Length, ct);
        buffer.Position = 0;
        if (read < 3 || head[0] != 0xFF || head[1] != 0xD8 || head[2] != 0xFF)
            throw new InvalidOperationException("Ảnh không phải định dạng JPEG.");

        var key = $"practice-face/{candidateId}/{sessionId}/{Guid.NewGuid():N}.jpg";

        // Bất biến của PracticeFaceImage: ghi sổ TRƯỚC rồi mới upload (chết giữa chừng → dòng trỏ
        // vào object vắng mặt, vô hại — purger tự lành).
        var ledger = new PracticeFaceImage
        {
            SessionId = sessionId,
            CandidateId = candidateId,
            StorageKey = key,
            CapturedAt = DateTime.UtcNow
        };
        _db.PracticeFaceImages.Add(ledger);
        await _db.SaveChangesAsync(ct);

        await _storage.UploadObjectAsync(key, buffer, "image/jpeg", ct);

        // AiServiceException ném ra ngoài (khác /decide-next vốn degrade im lặng): sổ + ảnh đã ghi
        // xong ở trên nên ném ở đây KHÔNG để lại gì mồ côi — an toàn để controller trả 502/504.
        var detected = await _detector.DetectAsync(key, ct);

        // Detect xong ⇒ ảnh hết việc. Xoá S3 TRƯỚC; thành công mới gỡ dòng sổ (cùng SaveChanges với
        // cờ bên dưới). Xoá hụt ⇒ GIỮ dòng để purger thử lại — thà retry còn hơn để object không ai
        // trỏ tới. Dùng CancellationToken.None: client ngắt kết nối giữa chừng không được biến ảnh
        // đã có kết quả thành rác chờ purger (mà purger mặc định TẮT).
        if (await TryDeleteObjectAsync(key))
            _db.PracticeFaceImages.Remove(ledger);

        // Lọc CHỈ giữ tín hiệu server-only đã cấp phép — chặn AIService (hoặc một client hỏng) trả
        // về thứ lạ (vd "face_mismatch") lọt vào bảng vốn không dành cho nó.
        var signals = detected.Signals.Where(FocusSignals.IsServerOnly).ToList();

        if (signals.Count > 0)
        {
            var total = await _db.PracticeFocusEvents.CountAsync(e => e.SessionId == sessionId, ct);
            if (total >= FocusSignals.MaxEventsPerSession)
            {
                _logger.LogDebug(
                    "Bỏ qua tín hiệu mặt (buổi {SessionId}): chạm trần {Max} dòng.",
                    sessionId, FocusSignals.MaxEventsPerSession);
            }
            else
            {
                foreach (var signal in signals)
                {
                    var note = signal switch
                    {
                        FocusSignals.NoFace => "Không thấy bạn trong khung hình",
                        FocusSignals.MultipleFaces => $"Có {detected.FaceCount} người trong khung hình",
                        _ => null
                    };
                    _db.PracticeFocusEvents.Add(new PracticeFocusEvent
                    {
                        SessionId = sessionId,
                        SignalType = signal,
                        Note = note,
                        OccurredAt = DateTime.UtcNow
                    });
                }
            }
        }

        // Một SaveChanges cho cả gỡ dòng sổ lẫn cờ mặt — không thay đổi gì thì EF không chạm DB.
        await _db.SaveChangesAsync(CancellationToken.None);

        return new FaceCheckResultResponse(detected.FaceCount, signals);
    }

    private async Task<bool> TryDeleteObjectAsync(string key)
    {
        try
        {
            await _storage.DeleteObjectAsync(key, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Xoá ảnh kiểm mặt '{Key}' thất bại — GIỮ dòng sổ để PracticeFaceImagePurger thử lại.", key);
            return false;
        }
    }
}
