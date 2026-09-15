using static System.Collections.Specialized.BitVector32;

namespace Isas.InterviewService.Entities
{
    public class FileRecord
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string FileType { get; set; } = null!;
        public string OriginalName { get; set; } = null!;
        public string StoragePath { get; set; } = null!;
        public string StorageBucket { get; set; } = null!;
        public string MimeType { get; set; } = null!;
        public long FileSize { get; set; }
        public string? ParsedText { get; set; }
        public string ParseStatus { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Soft-delete (2026-09-15). Vì sao KHÔNG xoá cứng: 3 FK ON DELETE RESTRICT trỏ vào bảng này
        // (practice_sessions.cv_id/jd_id, roadmaps.cv_id) nên CV/JD đã dùng cho một buổi là không xoá
        // được — và đường xoá cũ còn xoá object S3 TRƯỚC khi DB từ chối ⇒ đo prod 2026-09-15: 9/49
        // row "zombie" (row còn, file mất, tải 500, xoá 500). Đóng dấu ở đây giữ nguyên tham chiếu của
        // buổi/roadmap/cv_analyses (lịch sử, BC8, buổi đang chạy dở) và gỡ file khỏi mọi đường đọc
        // CỦA NGƯỜI DÙNG (list/get/download/parsed-text/dùng cho buổi mới). null = chưa xoá.
        public DateTime? DeletedAt { get; set; }

        // Navigation
        //public ICollection<Session> CvSessions { get; set; } = [];
        //public ICollection<Session> JdSessions { get; set; } = [];
    }
}
