namespace Isas.CampaignService.Models;

public class CampaignSlot
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public int Capacity { get; set; }
    public Campaign Campaign { get; set; } = null!;
}
