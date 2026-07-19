namespace Isas.InterviewService.DTOs;

/// <summary>
/// Một dòng trong `GET /api/files/files` (danh sách file CV/JD của chính user).
///
/// Trước đây endpoint trả THẲNG entity <c>FileRecord</c>, kéo theo 3 cột không được phép ra ngoài:
/// <list type="bullet">
///   <item><c>parsed_text</c> — TOÀN VĂN mọi CV/JD user từng upload. Một request danh sách kéo về
///   toàn bộ nội dung CV; vừa phình payload vừa lộ dữ liệu mà màn hình danh sách không cần
///   (đọc nội dung đã có endpoint riêng <c>GET /files/{id}/parsed-text</c>, owner-scoped).</item>
///   <item><c>storage_path</c> / <c>storage_bucket</c> — toạ độ nội bộ trong SeaweedFS. GEN-5 nói
///   lưu key chứ không lưu full URL; key cũng không có lý do gì phải rời khỏi service.</item>
/// </list>
///
/// DTO này được project NGAY TRONG SQL (<c>.Select(...)</c> trước <c>ToListAsync</c>) chứ không phải
/// nạp entity rồi map — nếu map sau khi nạp thì <c>parsed_text</c> vẫn bị đọc lên từ DB (và với cột
/// TEXT lớn là cả TOAST fetch), chỉ khác là bị giấu ở tầng JSON. Ẩn ở JSON không phải là mục tiêu;
/// KHÔNG ĐỌC mới là.
/// </summary>
public record FileRecordSummary(
    Guid Id,
    string FileType,
    string OriginalName,
    string MimeType,
    long FileSize,
    string ParseStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
