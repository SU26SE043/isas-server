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

            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = path,
                InputStream = stream,
                ContentType = file.ContentType,
                CannedACL = S3CannedACL.PublicRead
            };

            await _s3.PutObjectAsync(request, ct);
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

        public string GetUrl(string path) => $"{_serviceUrl}/{_bucket}/{path}";
    }
}
