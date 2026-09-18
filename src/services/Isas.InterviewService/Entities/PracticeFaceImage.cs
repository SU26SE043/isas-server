namespace Isas.InterviewService.Entities;

/// <summary>
/// SỔ THEO DÕI ảnh webcam B2C đã đẩy lên S3 — B2C coaching (2026-09-17, BC-6 ngoại lệ).
///
/// Mỗi lượt kiểm mặt gửi 1 ảnh JPEG; ảnh là VẬT TRUNG GIAN để AIService đọc, detect xong là
/// <c>PracticeFaceCheckService</c> xoá ngay (không màn nào đọc lại ảnh — DATA-3). Ở trạng thái
/// bình thường bảng này RỖNG; dòng còn lại = phần sót (AI lỗi/hết giờ, chết giữa chừng, xoá S3
/// hụt) chờ <c>PracticeFaceImagePurger</c>. Bất biến (mẫu <c>CampaignService.Models.FaceImage</c>):
/// <i>KHÔNG BAO GIỜ để một object tồn tại trong S3 mà không có dòng nào trỏ tới</i> — ghi sổ
/// TRƯỚC rồi mới upload (chết giữa chừng → dòng trỏ object vắng mặt, vô hại, purge tự lành);
/// xoá thì S3 TRƯỚC rồi mới xoá dòng (ngược lại tái tạo con bug BK25 bên B2B).
///
/// CỐ Ý KHÔNG có FK tới <c>practice_sessions</c> (khác <see cref="PracticeFocusEvent"/> vốn có FK
/// Cascade): đây là SỔ RETENTION độc lập với vòng đời buổi — cascade xoá dòng sổ khi buổi bị xoá
/// (huỷ/soft-delete) là tái tạo đúng "object mồ côi trong S3" mà bảng này sinh ra để chặn. Mọi
/// tham chiếu là Guid lỏng (GEN-2, mẫu <c>FaceImage.CampaignId</c>).
/// </summary>
public class PracticeFaceImage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }
    public Guid CandidateId { get; set; }

    /// <summary>S3 KEY (GEN-5: key chứ không phải full URL). UNIQUE — 1 object = 1 dòng.</summary>
    public string StorageKey { get; set; } = null!;

    /// <summary>Thời điểm SERVER nhận. Mốc DUY NHẤT job purge dùng để tính quá hạn.</summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}
