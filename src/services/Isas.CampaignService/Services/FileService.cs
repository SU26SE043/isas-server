using Amazon.S3;
using Amazon.S3.Model;
using Isas.CampaignService.Models;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Services
{
    public class FileService : IFileService
    {
        private readonly IAmazonS3 _s3;
        private readonly string _bucket;
        private readonly string _serviceUrl;

        public FileService(IAmazonS3 s3, IOptions<FileStorageOptions> options)
        {
            _s3 = s3;
            _bucket = options.Value.BucketName;
            _serviceUrl = options.Value.ServiceURL.TrimEnd('/');
        }

        public async Task<string> UploadAsync(IFormFile file, string path, CancellationToken ct = default)
        {
            await using var stream = file.OpenReadStream();

            // SeaweedFS (HTTP): buffer vào MemoryStream để SDK v4 biết ContentLength → ký single-chunk
            // (không STREAMING signature mà SeaweedFS từ chối cho upload browser). DisablePayloadSigning
            // không dùng được vì SeaweedFS chạy HTTP.
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = path,
                InputStream = buffer,
                ContentType = file.ContentType,
            };

            try
            {
                await _s3.PutObjectAsync(request, ct);
            }
            catch (AmazonS3Exception ex)
            {
                Console.WriteLine($"Message: {ex.Message}");
                Console.WriteLine($"ErrorCode: {ex.ErrorCode}");
                Console.WriteLine($"StatusCode: {ex.StatusCode}");
                throw;
            }
            return path;
        }

        public async Task DeleteAsync(string path, CancellationToken ct = default)
        {
            var request = new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = path
            };

            await _s3.DeleteObjectAsync(request, ct);
        }

        public async Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
        {
            var request = new GetObjectRequest
            {
                BucketName = _bucket,
                Key = path
            };
            var response = await _s3.GetObjectAsync(request, ct);
            return response.ResponseStream;
        }

        public string GetUrl(string path) => $"{_serviceUrl}/{_bucket}/{path}";
    }
}
