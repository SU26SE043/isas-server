namespace Isas.CampaignService.Services;

public sealed class EntitlementForbiddenException(string message) : InvalidOperationException(message);
