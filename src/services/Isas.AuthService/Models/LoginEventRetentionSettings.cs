namespace Isas.AuthService.Models;

/// <summary>FR18 — retention cho telemetry login, tắt được để deploy quan sát an toàn.</summary>
public sealed class LoginEventRetentionSettings
{
    public const string SectionName = "LoginEventRetention";
    public const int DefaultRetentionDays = 365;
    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = DefaultRetentionDays;
    public int ScanIntervalMinutes { get; set; } = 60;
    public int BatchSize { get; set; } = 5000;
    public int MaxBatchesPerRun { get; set; } = 20;
    public int EffectiveRetentionDays => RetentionDays > 0 ? RetentionDays : DefaultRetentionDays;
    public int EffectiveBatchSize => BatchSize > 0 ? BatchSize : 5000;
    public int EffectiveMaxBatches => MaxBatchesPerRun > 0 ? MaxBatchesPerRun : 20;
}
