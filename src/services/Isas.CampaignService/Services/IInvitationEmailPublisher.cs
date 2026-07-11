namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Job đẩy vào email queue cho 1 lời mời (D1). Worker gửi mail thật KHÔNG thuộc phạm vi D1.
    /// </summary>
    public record InvitationEmailJob(
        Guid InvitationId,
        Guid CampaignId,
        string Email,
        string Token,
        string CampaignTitle,
        DateTime? ExpiresAt);

    public interface IInvitationEmailPublisher
    {
        Task PublishAsync(InvitationEmailJob job, CancellationToken ct = default);
    }
}
