namespace Isas.InterviewService.Models;

public class FileStorageOptions
{
    // Tên section trong appsettings.json
    public const string SectionName = "SeaweedFS";

    // Endpoint S3 gateway của SeaweedFS
    // Dev (chạy ngoài container): http://localhost:8333
    // Compose (trong container):  http://seaweedfs:8333
    public string ServiceURL { get; set; } = null!;

    public string AccessKey { get; set; } = null!;
    public string SecretKey { get; set; } = null!;

    // Bucket lưu file (CV/JD + audio answer dùng chung bucket, tách bằng prefix key)
    public string BucketName { get; set; } = "isas-files";

    // Bắt buộc true cho S3-compatible không phải AWS thật (path-style URL)
    public bool ForcePathStyle { get; set; } = true;

    // SeaweedFS local thường chạy http (không TLS) -> true ở dev
    public bool UseHttp { get; set; } = true;

    // Region giả, SeaweedFS không kiểm nhưng SDK yêu cầu có
    public string Region { get; set; } = "us-east-1";

    // Giới hạn kích thước file upload (MB) - chặn ở tầng nghiệp vụ
    public int MaxFileSizeMb { get; set; } = 50;

    // Thời hạn presigned URL khi cho tải file riêng tư (phút)
    public int PresignedUrlExpiryMinutes { get; set; } = 15;
}