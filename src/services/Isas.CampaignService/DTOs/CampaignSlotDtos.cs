namespace Isas.CampaignService.DTOs;

public sealed class CreateCampaignSlotRequest { public DateTime StartsAt { get; set; } public DateTime EndsAt { get; set; } public int Capacity { get; set; } }
public sealed class UpdateCampaignSlotRequest { public DateTime StartsAt { get; set; } public DateTime EndsAt { get; set; } public int Capacity { get; set; } }
public sealed class CampaignSlotResponse { public Guid Id { get; set; } public DateTime StartsAt { get; set; } public DateTime EndsAt { get; set; } public int Capacity { get; set; } public int AssignedCount { get; set; } public int StartedCount { get; set; } }
