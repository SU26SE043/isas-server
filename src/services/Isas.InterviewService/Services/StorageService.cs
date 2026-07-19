using Amazon.S3;
using Amazon.S3.Model;
using Isas.InterviewService.DTOs;
using Microsoft.EntityFrameworkCore;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

public class StorageService : IStorageService
{
    private readonly ILogger<StorageService> _logger;
    private readonly IAmazonS3 _s3;
    private readonly FileStorageOptions _opts;
    private readonly InterviewDbContext _db;

    public StorageService(ILogger<StorageService> logger, IAmazonS3 s3, IOptions<FileStorageOptions> opts, InterviewDbContext db)
    {
        _logger = logger;
        _s3 = s3;
        _opts = opts.Value;
        _db = db;
    }

    public async Task<Stream> DownloadAsync(string storagePath, CancellationToken ct = default)
    {
        var request = new GetObjectRequest
        {
            BucketName = _opts.BucketName,
            Key = storagePath
        };

        var response = await _s3.GetObjectAsync(request, ct);
        return response.ResponseStream;
    }

    public string GetPresignedUrl(string storagePath, int expiryMinutes = 60)
    {
        return $"{_opts.ServiceURL}/{_opts.BucketName}/{storagePath}";
    }

    public async Task<string> UploadAsync(Stream fileStream, string fileType, Guid userId, Guid fileId, string ext, string contentType = "application/octet-stream", CancellationToken ct = default)
    {
        var key = BuildKey(fileType, userId, fileId, ext);

        // SeaweedFS (HTTP) KHÔNG hỗ trợ AWS chunked/streaming payload signature mà SDK v4 dùng khi
        // stream KHÔNG rõ length (upload từ browser) → "signature does not match". DisablePayloadSigning
        // không dùng được (SeaweedFS chạy HTTP, SDK bắt buộc HTTPS). Fix: buffer vào MemoryStream
        // (seekable + biết Length) → SDK ký single-chunk payload chuẩn → SeaweedFS chấp nhận.
        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        // Content-Type từ browser có param (vd "audio/webm;codecs=opus"). Dấu ';' phá canonicalization
        // chữ ký SigV4 của SeaweedFS → "signature does not match". Bỏ param, giữ media-type gốc.
        var cleanContentType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Split(';')[0].Trim();

        var request = new PutObjectRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            InputStream = buffer,
            ContentType = cleanContentType,
            AutoCloseStream = false,
        };

        request.Metadata.Add("x-amz-meta-uploaded-by", userId.ToString());
        request.Metadata.Add("x-amz-meta-file-type", fileType);
        
        var response = await _s3.PutObjectAsync(request, ct);

        _logger.LogInformation("Uploaded {Key} to bucket {Bucket}. HttpStatus={Status}", key, _opts.BucketName, response.HttpStatusCode);

        return key;
    }

    // Tự động convert Guid sang string khi nối chuỗi làm S3 Key Path
    private static string BuildKey(string fileType, Guid userId, Guid fileId, string ext) 
        => $"{fileType.ToLower()}/{userId}/{fileId}.{ext.TrimStart('.').ToLower()}";

    public async Task<FileRecord> SaveMetadata(Guid fileId, Guid userId, string fileType, string originalName, string storagePath, string storageBucket, string mimeType, long fileSize, CVParseResult? parsedCv, CancellationToken ct = default)
    {
        var fileRecord = new FileRecord
        {
            Id = fileId,
            UserId = userId,
            FileType = fileType,
            OriginalName = originalName,
            StoragePath = storagePath,
            StorageBucket = storageBucket,
            MimeType = mimeType,
            FileSize = fileSize,
            ParsedText = parsedCv?.RawText,
            ParseStatus = parsedCv is not null ? "completed" : "failed",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.FileRecords.Add(fileRecord);
        await _db.SaveChangesAsync(ct);

        return fileRecord;
    }

    public async Task<FileRecord?> GetMetadata(Guid fileId, CancellationToken ct = default)
    {
        return await _db.FileRecords.FirstOrDefaultAsync(f => f.Id == fileId, ct);
    }

    public async Task<string> GetParseTextAsync(Guid fileId, CancellationToken ct = default)
    {
        var file = await _db.FileRecords.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        return file?.ParsedText ?? string.Empty;
    }

    /// <summary>
    /// Như <see cref="GetParseTextAsync"/> nhưng CHỈ đọc file của chính chủ.
    /// interview.md §Validation yêu cầu `cvId`/`jdId` phải thuộc về user, nhưng luồng tạo session
    /// KHÔNG kiểm — candidate A truyền `cvId` của B thì CV của B lọt vào prompt sinh câu hỏi, tức A
    /// đọc được nội dung CV người khác qua các câu hỏi (bắt khi rà e2e 2026-07-18).
    /// File của người khác → trả rỗng, **y như file không tồn tại** (hành vi sẵn có với id lạ): theo
    /// tiền lệ BK15 "non-owner không xác nhận sự tồn tại", và tránh đổi status code làm FE đăng xuất
    /// oan (PracticeController map UnauthorizedAccessException → 401 → interceptor đá về /auth/login).
    /// </summary>
    public async Task<string> GetOwnedParsedTextAsync(Guid fileId, Guid ownerId, CancellationToken ct = default)
    {
        var file = await _db.FileRecords
            .FirstOrDefaultAsync(f => f.Id == fileId && f.UserId == ownerId, ct);
        return file?.ParsedText ?? string.Empty;
    }

    /// <summary>
    /// Danh sách file của một user — keyset-paged (mẫu DB8/DB31) + project gọn ngay trong SQL.
    ///
    /// Trước: <c>Where(user_id).ToListAsync()</c> trả NGUYÊN entity, không giới hạn số dòng. Hai vấn đề
    /// độc lập nhau, sửa cả hai ở đây:
    /// (1) không phân trang ⇒ user upload càng nhiều thì payload càng lớn, không có trần;
    /// (2) <c>SELECT *</c> kéo cả <c>parsed_text</c> = toàn văn mọi CV/JD đã upload. Điểm mấu chốt là
    /// <see cref="FileRecordSummary"/> được <c>Select</c> TRƯỚC <c>ToListAsync</c> nên EF sinh
    /// <c>SELECT id, file_type, …</c> — <c>parsed_text</c> KHÔNG bao giờ rời khỏi DB. (Map sau khi nạp
    /// entity thì cột vẫn bị đọc lên, chỉ là không serialize ra JSON — không đạt mục đích.)
    ///
    /// <paramref name="fileType"/> lọc push-down xuống SQL (không lọc sau khi đã lấy trang, vì như vậy
    /// trang có thể rỗng dù còn dữ liệu khớp ở trang sau).
    /// </summary>
    public async Task<KeysetPage<FileRecordSummary>> GetFilesByUserId(
        Guid userId, string? fileType = null, string? cursor = null, int? limit = null,
        CancellationToken ct = default)
    {
        var take = KeysetPaging.ClampLimit(limit);
        var cur = KeysetCursor.Decode(cursor);

        var query = _db.FileRecords.AsNoTracking().Where(f => f.UserId == userId);

        if (!string.IsNullOrWhiteSpace(fileType))
        {
            var wanted = fileType.Trim();
            query = query.Where(f => f.FileType == wanted);
        }

        // Keyset (CreatedAt DESC, Id DESC): lấy phần ĐUÔI sau con trỏ. Id làm tie-break nên hai file
        // trùng created_at vẫn có thứ tự tổng, không bị lặp/nhảy dòng giữa các trang.
        if (cur is not null)
            query = query.Where(f => f.CreatedAt < cur.CreatedAt
                || (f.CreatedAt == cur.CreatedAt && f.Id.CompareTo(cur.Id) < 0));

        var rows = await query
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .Take(take)
            .Select(f => new FileRecordSummary(
                f.Id, f.FileType, f.OriginalName, f.MimeType,
                f.FileSize, f.ParseStatus, f.CreatedAt, f.UpdatedAt))
            .ToListAsync(ct);

        // Đầy trang ⇒ CÓ THỂ còn dòng nữa → phát cursor. Chưa đầy ⇒ chắc chắn hết → null.
        var next = rows.Count == take
            ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;
        return new KeysetPage<FileRecordSummary>(rows, next);
    }

    public async Task<FileRecord> UpdateFileRecord(Guid fileId, Stream stream, string originalName, long fileSize, string contentType, CVParseResult? parsedCv, CancellationToken ct = default)
    {
        var fileRecord = await GetMetadata(fileId, ct) 
            ?? throw new KeyNotFoundException("File not found");

        // overwrite object using same key
        await _s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = fileRecord.StorageBucket,
                Key = fileRecord.StoragePath,
                InputStream = stream,
                ContentType = contentType,
                AutoCloseStream = true
            },
            ct);

        fileRecord.OriginalName = originalName;
        fileRecord.FileSize = fileSize;
        fileRecord.MimeType = contentType;
        fileRecord.UpdatedAt = DateTime.UtcNow;
        fileRecord.ParsedText = parsedCv?.RawText;

        await _db.SaveChangesAsync(ct);

        return fileRecord;
    }

    public async Task<bool> DeleteFileRecord(Guid fileId, CancellationToken ct = default)
    {
        var fileRecord = await _db.FileRecords.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (fileRecord == null)
        {
            return false;
        }

        await _s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = fileRecord.StorageBucket,
            Key = fileRecord.StoragePath
        }, ct);

        _db.FileRecords.Remove(fileRecord);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}