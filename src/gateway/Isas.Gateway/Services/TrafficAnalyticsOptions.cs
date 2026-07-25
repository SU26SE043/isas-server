namespace Isas.Gateway.Services;
public sealed class TrafficAnalyticsOptions
{
    public const string SectionName = "Analytics";
    public bool Enabled { get; set; }
    public string? SinkBaseUrl { get; set; }
    public int FlushIntervalSeconds { get; set; } = 300;
    public int MaxPendingWindows { get; set; } = 3;
}
