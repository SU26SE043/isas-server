using Isas.InterviewService.DTOs;
using Isas.InterviewService.Models;

namespace Isas.InterviewService.Services
{
    public interface IStorageService
    {
        Task<string> UploadAsync(Stream fileStream, string fileType, string userId, string fileId, string ext, string contentType = "application/octet-stream", CancellationToken ct = default);
        Task<Stream> DownloadAsync(string storagePath, CancellationToken ct = default);
        Task DeleteAsync(string storagePath, CancellationToken ct = default);
        string GetPresignedUrl(string storagePath, int expiryMinutes = 60);
        Task<FileRecord> SaveMetadata(string fileId, string userId, string fileType, string originalName, string storagePath, string storageBucket, string mimeType, long fileSize, CVParseResult? parsedCv, CancellationToken ct = default);

    }
}
