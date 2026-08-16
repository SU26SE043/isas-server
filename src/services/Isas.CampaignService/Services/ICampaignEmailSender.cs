namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Gửi email mời ứng viên (magic-link) cho 1 lời mời campaign.
    /// Implementation dựng subject + HTML body (kèm link + hạn) rồi gửi qua SMTP.
    /// Tách interface để <see cref="InvitationEmailConsumer"/> compose link + mock trong test
    /// (không cần SMTP thật).
    /// </summary>
    public interface ICampaignEmailSender
    {
        Task SendInvitationEmailAsync(
            string toEmail,
            string campaignTitle,
            string joinLink,
            DateTime? expiresAt,
            DateTime? slotStartsAt,
            DateTime? slotEndsAt,
            CancellationToken ct = default);
    }
}
