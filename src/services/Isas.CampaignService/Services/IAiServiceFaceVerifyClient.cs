namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Kết quả AIService trả về khi so ảnh live ↔ ảnh tham chiếu (SEC-2).
    /// <c>Signals</c> ⊂ { no_face, multiple_faces, face_mismatch } — mỗi tín hiệu → 1 cờ session_flags cho HR.
    /// </summary>
    public record FaceVerifyResult(int FaceCount, bool Match, float Score, IReadOnlyList<string> Signals);

    /// <summary>
    /// Gọi AIService POST /api/v1/face-verify (đồng bộ). AIService đọc CHUNG bucket SeaweedFS →
    /// nhận KEY (không truyền ảnh). Lỗi hạ tầng/HTTP → ném <see cref="DownstreamServiceException"/> (không nuốt).
    /// </summary>
    public interface IAiServiceFaceVerifyClient
    {
        Task<FaceVerifyResult> VerifyAsync(
            string referenceImageKey, string liveImageKey, double? threshold = null, CancellationToken ct = default);
    }
}
