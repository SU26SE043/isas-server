using Amazon.S3;
using Isas.InterviewService.Models;
using Microsoft.Extensions.Options;

public class BucketInitializer : IHostedService
{
    private readonly IAmazonS3 _s3;
    private readonly IOptions<FileStorageOptions> _opts;
    private readonly ILogger<BucketInitializer> _logger;

    public BucketInitializer(IAmazonS3 s3, IOptions<FileStorageOptions> opts, ILogger<BucketInitializer> logger)
    {
        _s3 = s3;
        _opts = opts;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var bucketName = _opts.Value.BucketName;

        try
        {
            var buckets = await _s3.ListBucketsAsync(ct);
            var exists = buckets.Buckets?.Any(b => b.BucketName == bucketName) ?? false;

            if (exists)
            {
                _logger.LogInformation("Bucket '{Bucket}' already exists, skipping creation.", bucketName);
                return;
            }

            await _s3.PutBucketAsync(bucketName, ct);
            _logger.LogInformation("Bucket '{Bucket}' created successfully.", bucketName);
        }
        catch (AmazonS3Exception ex) when (ex is AmazonS3Exception { ErrorCode: "BucketAlreadyExists" or "BucketAlreadyOwnedByYou" })
        {
            // Safe to ignore — bucket is there, which is all we need
            _logger.LogInformation("Bucket '{Bucket}' already exists (caught on create).", bucketName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize bucket '{Bucket}'.", bucketName);
            throw;
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}