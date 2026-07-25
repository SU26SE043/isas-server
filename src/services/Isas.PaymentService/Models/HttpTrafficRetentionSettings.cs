namespace Isas.PaymentService.Models;
public sealed class HttpTrafficRetentionSettings
{
    public const string SectionName = "HttpTrafficRetention";
    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = 90;
    public int ScanIntervalMinutes { get; set; } = 60;
}
