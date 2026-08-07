namespace Isas.CampaignService.Models
{
    /// <summary>
    /// BK25 — SỔ THEO DÕI ảnh sinh trắc học đã đẩy lên S3 (DATA-3: "lưu S3 key, có retention + purge").
    ///
    /// Trước task này, <c>face-check</c> upload 1 ảnh khuôn mặt SỐNG mỗi ~30 giây suốt buổi thi rồi
    /// VỨT key đi — không cột nào trong toàn schema Campaign trỏ tới nó. Hệ quả không phải "tốn đĩa"
    /// mà là <b>không liệt kê nổi, không join nổi</b>: muốn dọn cũng không biết dọn cái gì. Bảng này
    /// tồn tại để mỗi object sinh trắc trong S3 luôn có đúng một dòng trỏ tới.
    ///
    /// <b>Bất biến của cả tính năng</b> (quyết định thứ tự ghi/xoá ở cả 2 đầu):
    /// <i>KHÔNG BAO GIỜ để một object tồn tại trong S3 mà không có dòng nào trỏ tới.</i>
    ///  • TẠO: ghi dòng này TRƯỚC rồi mới upload. Chết giữa chừng → dòng trỏ vào object không tồn tại
    ///    (vô hại: purge gọi DeleteObject lên key vắng mặt là no-op, rồi dọn dòng).
    ///  • XOÁ: xoá object S3 TRƯỚC rồi mới xoá dòng. Chết giữa chừng → dòng còn, vòng sau thử lại.
    ///    Ngược lại (xoá dòng trước) = tái tạo đúng con bug này. Cùng lập luận với
    ///    <c>KnowledgeService.DeleteAsync</c> bên Interview (xoá vector Qdrant trước, metadata sau).
    ///
    /// <b>CỐ Ý KHÔNG có FK tới <c>campaigns</c></b> dù DB9 đặt FK cho <c>session_flags</c>: nav bắt
    /// buộc tới Campaign kéo theo query filter soft-delete (DB13) — mà campaign ĐÃ soft-delete chính
    /// là lúc cần purge nhất, filter sẽ giấu đúng những dòng đó khỏi job dọn. Đây là SỔ RETENTION,
    /// không phải dữ liệu nghiệp vụ; mọi tham chiếu để Guid lỏng (GEN-2).
    /// </summary>
    public class FaceImage
    {
        public Guid Id { get; set; }

        // Ref lỏng (GEN-2). CampaignId+CandidateId là khoá để tìm lại membership khi purge ảnh THAM CHIẾU
        // (campaign_membership UNIQUE(campaign_id, candidate_id)).
        public Guid CampaignId { get; set; }
        public Guid CandidateId { get; set; }

        // Ref lỏng → Interview. Ảnh LIVE luôn có buổi thi; ảnh THAM CHIẾU (enroll) không gắn buổi nào → null.
        public Guid? SessionId { get; set; }

        public FaceImageKind Kind { get; set; }

        // S3 KEY (GEN-5: KEY chứ không phải full URL). UNIQUE — 1 object = 1 dòng; nhờ đó enroll lại
        // đúng cùng key chỉ cập nhật CapturedAt thay vì đẻ thêm dòng trỏ cùng chỗ.
        public string StorageKey { get; set; } = null!;

        // Thời điểm chụp (server nhận). Đây là mốc DUY NHẤT job purge dùng để tính quá hạn.
        public DateTime CapturedAt { get; set; }
    }

    /// <summary>Loại ảnh sinh trắc (lưu string — GEN-2).</summary>
    public enum FaceImageKind
    {
        /// <summary>Ảnh giám sát chụp trong lúc thi (~30s/lần) — nguồn phình chính.</summary>
        Live = 0,

        /// <summary>Ảnh tham chiếu lúc enroll — DATA-2: 1 bản/ứng viên/campaign.</summary>
        Reference = 1
    }
}
