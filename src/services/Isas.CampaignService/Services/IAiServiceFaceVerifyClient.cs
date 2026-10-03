namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Kết quả AIService trả về khi so ảnh live ↔ ảnh tham chiếu (SEC-2).
    /// <c>Signals</c> ⊂ { no_face, multiple_faces, face_mismatch } — mỗi tín hiệu → 1 cờ session_flags cho HR.
    /// </summary>
    public record FaceVerifyResult(int FaceCount, bool Match, float Score, IReadOnlyList<string> Signals);

    /// <summary>
    /// AC2 — kết quả AIService <c>POST /api/v1/face-detect</c>: CHỈ đếm mặt trên 1 ảnh (không so khớp).
    /// <c>Signals</c> ⊂ { no_face, multiple_faces } — Campaign enroll không dùng tới, giữ để log.
    /// </summary>
    public record FaceDetectResult(int FaceCount, IReadOnlyList<string> Signals);

    /// <summary>
    /// Gọi AIService POST /api/v1/face-verify (đồng bộ). AIService đọc CHUNG bucket SeaweedFS →
    /// nhận KEY (không truyền ảnh). Lỗi hạ tầng/HTTP → ném <see cref="DownstreamServiceException"/> (không nuốt).
    /// </summary>
    public interface IAiServiceFaceVerifyClient
    {
        Task<FaceVerifyResult> VerifyAsync(
            string referenceImageKey, string liveImageKey, double? threshold = null, CancellationToken ct = default);

        /// <summary>
        /// AC2 — đếm khuôn mặt trên ảnh <paramref name="imageKey"/> (AIService đọc chung bucket, GEN-5).
        /// Dùng ĐÚNG bộ dò mặt mà <c>/face-verify</c> dùng để đếm mặt trên ảnh mốc ⇒ ảnh qua được cửa
        /// enroll là ảnh mà face-check sau này đọc được.
        /// Lỗi hạ tầng / non-2xx / body hỏng / HẾT GIỜ → <see cref="DownstreamServiceException"/>.
        /// Người gọi tự huỷ (<paramref name="ct"/>) → <see cref="OperationCanceledException"/> đi thẳng
        /// lên, KHÔNG bị đổi thành lỗi hạ tầng (người gọi đã bỏ đi, không có gì để "mở cửa" cho ai).
        /// </summary>
        Task<FaceDetectResult> DetectAsync(string imageKey, CancellationToken ct = default);
    }
}
