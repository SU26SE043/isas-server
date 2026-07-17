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

        // DB2b — OutboxDispatcher publish payload NGUYÊN từ outbox-row (không reconstruct job). messageId
        // → BasicProperties.MessageId. Lỗi (broker down) → ném ra để dispatcher giữ published_at null + Attempts++.
        Task PublishRawAsync(string payloadJson, string messageId, CancellationToken ct = default);
    }
}
