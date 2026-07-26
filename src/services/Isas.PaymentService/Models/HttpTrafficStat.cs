namespace PaymentService.Models;

/// <summary>FR18 — cửa sổ telemetry public API từ Gateway, append-only (không phải log từng request).</summary>
public sealed class HttpTrafficStat
{
    public Guid Id { get; set; }
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public string RouteId { get; set; } = null!;
    public string StatusClass { get; set; } = null!;
    public int Requests { get; set; }
    public long SumDurationMs { get; set; }
    public int MaxDurationMs { get; set; }
    public DateTime CreatedAt { get; set; }
}
