namespace Isas.CampaignService.Models;

/// <summary>Rollout gate for B2B tier entitlement enforcement.</summary>
public sealed class TieringSettings
{
    public const string SectionName = "Tiering";
    public bool Enabled { get; set; }
}
