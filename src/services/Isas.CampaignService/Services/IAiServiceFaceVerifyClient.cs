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
    /// AIService trả <b>422 <c>IMAGE_UNREADABLE</c></b>: ảnh gửi vào KHÔNG giải mã được (tệp hỏng / sai
    /// định dạng). Đây là lỗi của DỮ LIỆU, không phải của hạ tầng ⇒ CỐ Ý KHÔNG kế thừa
    /// <see cref="DownstreamServiceException"/>: kế thừa thì mọi khối <c>catch (DownstreamServiceException)</c>
    /// đang có (face-enroll FAIL-OPEN, face-check 502) bắt nhầm nó — đúng con bug dev 03/10, mốc rác được
    /// nhận như thể AIService chỉ chập chờn.
    /// <para><see cref="Image"/> = ảnh nào hỏng, theo tên khoá request: <c>"reference"</c> · <c>"live"</c>
    /// (<c>/face-verify</c>) · <c>"image"</c> (<c>/face-detect</c> — chỉ có một ảnh).</para>
    /// </summary>
    public class ImageUnreadableException : Exception
    {
        public const string Reference = "reference";
        public const string Live = "live";

        public string Image { get; }

        public ImageUnreadableException(string image)
            : base($"AIService không giải mã được ảnh '{image}'.")
        {
            Image = image;
        }
    }

    /// <summary>
    /// Gọi AIService POST /api/v1/face-verify (đồng bộ). AIService đọc CHUNG bucket SeaweedFS →
    /// nhận KEY (không truyền ảnh). Lỗi hạ tầng/HTTP → ném <see cref="DownstreamServiceException"/> (không nuốt).
    /// Riêng 422 mang mã <c>IMAGE_UNREADABLE</c> → <see cref="ImageUnreadableException"/>; 422 KHÁC (pydantic —
    /// khoá JSON lệch hợp đồng) vẫn là <see cref="DownstreamServiceException"/>, để lệch hợp đồng không biến
    /// thành "chặn mọi ứng viên".
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
        /// Ảnh không giải mã được (422 <c>IMAGE_UNREADABLE</c>) → <see cref="ImageUnreadableException"/>.
        /// Người gọi tự huỷ (<paramref name="ct"/>) → <see cref="OperationCanceledException"/> đi thẳng
        /// lên, KHÔNG bị đổi thành lỗi hạ tầng (người gọi đã bỏ đi, không có gì để "mở cửa" cho ai).
        /// </summary>
        Task<FaceDetectResult> DetectAsync(string imageKey, CancellationToken ct = default);
    }
}
