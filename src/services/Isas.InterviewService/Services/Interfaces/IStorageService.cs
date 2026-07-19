using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.Shared.Pagination;

namespace Isas.InterviewService.Services.Interfaces;

public interface IStorageService
{
    // Bọc Guid cho userId và fileId để tầng Service ngoài truyền thẳng Guid vào, không cần .ToString() nữa
    Task<string> UploadAsync(Stream fileStream, string fileType, Guid userId, Guid fileId, string ext, string contentType = "application/octet-stream", CancellationToken ct = default);
    Task<Stream> DownloadAsync(string storagePath, CancellationToken ct = default);
    string GetPresignedUrl(string storagePath, int expiryMinutes = 60);
    
    // Toàn bộ tương tác DB dùng thuần Guid
    Task<FileRecord> SaveMetadata(Guid fileId, Guid userId, string fileType, string originalName, string storagePath, string storageBucket, string mimeType, long fileSize, CVParseResult? parsedCv, CancellationToken ct = default);
    Task<FileRecord?> GetMetadata(Guid fileId, CancellationToken ct = default);
    Task<string> GetParseTextAsync(Guid fileId, CancellationToken ct = default); // Đã đổi sang đuôi Async

    // Bản owner-scoped: dùng ở luồng tạo session B2C để không đọc file của người khác (xem StorageService).
    Task<string> GetOwnedParsedTextAsync(Guid fileId, Guid ownerId, CancellationToken ct = default);
    // Danh sách file của user: keyset-paged + project gọn (BỎ parsed_text/storage_path/storage_bucket).
    // Trả DTO chứ không phải entity — xem StorageService.GetFilesByUserId để biết vì sao projection
    // phải nằm trong SQL chứ không phải map sau khi nạp.
    Task<KeysetPage<FileRecordSummary>> GetFilesByUserId(
        Guid userId, string? fileType = null, string? cursor = null, int? limit = null,
        CancellationToken ct = default);
    Task<FileRecord> UpdateFileRecord(Guid fileId, Stream stream, string originalName, long fileSize, string contentType, CVParseResult? parsedCv, CancellationToken ct = default);
    Task<bool> DeleteFileRecord(Guid fileId, CancellationToken ct = default);
}