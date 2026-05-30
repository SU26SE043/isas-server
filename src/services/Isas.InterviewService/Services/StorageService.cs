using Amazon.S3;
using Amazon.S3.Model;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Models;
using System.Net.Mime;
using static System.Net.Mime.MediaTypeNames;

namespace Isas.InterviewService.Services
{
    public class StorageService : IStorageService
    {
        private readonly ILogger<StorageService> _logger;
        private readonly IAmazonS3 _s3;
        private readonly FileStorageOptions _opts;
        private readonly InterviewDbContext _db;
        public StorageService(ILogger<StorageService> logger, IAmazonS3 s3, FileStorageOptions opts, InterviewDbContext db)
        {
            _logger = logger;
            _s3 = s3;
            _opts = opts;
            _db = db;
        }

        public Task DeleteAsync(string storagePath, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public string GetPresignedUrl(string storagePath, int expiryMinutes = 60)
        {
            return $"{_opts.ServiceURL}/{_opts.BucketName}/{storagePath}";
        }

        public async Task<string> UploadAsync(Stream fileStream, string fileType, string userId, string fileId, string ext, string contentType = "application/octet-stream", CancellationToken ct = default)
        {
            var key = BuildKey(fileType, userId, fileId, ext);

            var request = new PutObjectRequest
            {
                BucketName = _opts.BucketName,
                Key = key,
                InputStream = fileStream,
                ContentType = contentType,
                AutoCloseStream = false
            };

            request.Metadata.Add("x-amz-meta-uploaded-by", userId);
            request.Metadata.Add("x-amz-meta-file-type", fileType);

            var response = await _s3.PutObjectAsync(request, ct);

            _logger.LogInformation("Uploaded {Key} to bucket {Bucket}. HttpStatus={Status}", key, _opts.BucketName, response.HttpStatusCode);

            return key;
        }

        private static string BuildKey(string fileType, string userId, string fileId, string ext) => $"{fileType.ToLower()}/{userId}/{fileId}.{ext.TrimStart('.').ToLower()}";

        public async Task<FileRecord> SaveMetadata(string fileId, string userId, string fileType, string originalName, string storagePath, string storageBucket, string mimeType, long fileSize, CVParseResult? parsedCv, CancellationToken ct = default)
        {
            var fileRecord = new FileRecord
            {
                Id = Guid.Parse(fileId),
                UserId = Guid.TryParse(userId, out var uid) ? uid : Guid.Empty,
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

            _db.Files.Add(fileRecord);
            await _db.SaveChangesAsync(ct);

            return fileRecord;
        }
    }
}
