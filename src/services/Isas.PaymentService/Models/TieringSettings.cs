namespace PaymentService.Models;

public sealed class TieringSettings
{
    public const string SectionName = "Tiering";
    public bool Enabled { get; set; }
    public bool AllowUnlimitedPlans { get; set; }
}
