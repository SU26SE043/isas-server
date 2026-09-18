namespace Isas.InterviewService.Services.Interfaces;

// B2C coaching — typed HttpClient gọi AIService `/face-detect` (máy-máy, X-Internal-Token).
// Detect-only: đếm mặt trên 1 ảnh, KHÔNG so khớp danh tính (khác /face-verify của B2B).
public interface IAiServiceFaceDetector
{
    Task<FaceDetectResult> DetectAsync(string imageKey, CancellationToken ct = default);
}

public record FaceDetectResult(int FaceCount, IReadOnlyList<string> Signals);
