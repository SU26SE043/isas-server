namespace Isas.InterviewService.DTOs
{
    public class UploadFileRequest
    {
        public string FileType { get; set; } = default!;
    }

    public class UploadFileResponse
    {
        public string FileId { get; init; } = default!;
        public string StoragePath { get; init; } = default!;
        public string PresignedUrl { get; init; } = default!;
        public string FileName { get; init; } = default!;
        public long SizeBytes { get; init; }
        public CVParseResult? ParsedCv { get; init; }
    }
}
