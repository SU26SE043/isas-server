using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

/// <summary>
/// B2C coaching (2026-09-17) — kiểm mặt định kỳ trong buổi luyện. Detect-only (đếm mặt), KHÔNG so
/// khớp danh tính. Tách khỏi <see cref="IPracticeService"/> có chủ đích: thêm tham số vào ctor của
/// <c>PracticeService</c> sẽ buộc sửa 47 lời gọi <c>new PracticeService(</c> rải trong 34 file test.
/// </summary>
public interface IPracticeFaceCheckService
{
    /// <summary>
    /// null = "không áp dụng" (buổi tắt theo dõi / B2B / đã kết thúc) — KHÔNG upload, KHÔNG gọi AI.
    /// Ném: KeyNotFoundException (buổi không tồn tại) · UnauthorizedAccessException (không phải
    /// buổi của mình) · InvalidOperationException (ảnh rỗng/quá lớn/không phải JPEG).
    /// </summary>
    Task<FaceCheckResultResponse?> RecordFaceCheckAsync(
        Guid candidateId, Guid sessionId, Stream image, long length, CancellationToken ct = default);
}
