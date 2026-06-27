namespace Isas.InterviewService.DTOs
{
    public class UploadFileRequest
    {
        public string FileType { get; set; } = default!;
    }

    public class UploadFileResponse
    {
        public string FileId { get; init; } = default!;
        public string FileType { get; init; } = default!;
        public string OriginalName { get; init; } = default!;
        public string MimeType { get; init; } = default!;
        public long FileSize { get; init; }
        public string ParsedStatus { get; init; } = default!;
        public DateTime CreatedAt { get; init; } = default!;
    }
}
