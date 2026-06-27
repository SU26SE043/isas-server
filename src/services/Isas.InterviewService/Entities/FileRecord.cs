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

        // Navigation
        //public ICollection<Session> CvSessions { get; set; } = [];
        //public ICollection<Session> JdSessions { get; set; } = [];
    }
}
