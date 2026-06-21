using Amazon.S3;
using Amazon.S3.Model;
using Isas.InterviewService.Models;
using Microsoft.Extensions.Options;

public class BucketInitializer : IHostedService
{
    private readonly IAmazonS3 _s3;
    private readonly IOptions<FileStorageOptions> _opts;
    private readonly ILogger<BucketInitializer> _logger;

    public BucketInitializer(
        IAmazonS3 s3,
        IOptions<FileStorageOptions> opts,
        ILogger<BucketInitializer> logger)
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
            var exists = await Amazon.S3.Util.AmazonS3Util
                .DoesS3BucketExistV2Async(_s3, bucketName);

            if (exists)
            {
                _logger.LogInformation("Bucket '{Bucket}' đã tồn tại, bỏ qua tạo.", bucketName);
                return;
            }

            await _s3.PutBucketAsync(new PutBucketRequest { BucketName = bucketName }, ct);
            _logger.LogInformation("Đã tạo bucket '{Bucket}'.", bucketName);
        }
        catch (AmazonS3Exception ex) when (
            ex.ErrorCode is "BucketAlreadyExists" or "BucketAlreadyOwnedByYou")
        {
            _logger.LogInformation("Bucket '{Bucket}' đã tồn tại (bắt khi tạo).", bucketName);
        }
        catch (Exception ex)
        {
            // KHÔNG throw: storage có thể chưa sẵn sàng lúc app khởi động.
            // Để app vẫn lên; bucket sẽ được tạo lazy ở lần upload đầu (StorageService.EnsureBucket).
            _logger.LogError(ex,
                "Khởi tạo bucket '{Bucket}' thất bại. App vẫn chạy; sẽ thử tạo lại khi upload.",
                bucketName);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}