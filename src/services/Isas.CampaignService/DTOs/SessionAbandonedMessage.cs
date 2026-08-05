namespace Isas.CampaignService.DTOs;

/// <summary>Local contract for Interview's <c>session.abandoned</c> event.</summary>
public class SessionAbandonedMessage
{
    public Guid SessionId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid CandidateId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime AbandonedAt { get; set; }
}
