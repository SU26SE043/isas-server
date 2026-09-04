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
        /// <param name="startsAt">CMP1-B4 — giờ campaign MỞ (campaign.StartsAt, KHÁC slotStartsAt/
        /// expiresAt). null hoặc đã ở quá khứ ⇒ không in dòng "Phỏng vấn mở từ".</param>
        /// <param name="orgName">Tên công ty mời — resolve TRƯỚC ở nơi tạo job (fail-soft, có thể
        /// null). null ⇒ chữ ký giữ nguyên "Đội ngũ ISAS" (KHÔNG vỡ).</param>
        /// <param name="faceVerifyEnabled">true ⇒ thư nói rõ buổi phỏng vấn cần camera + micro.</param>
        /// <param name="timeLimitMinutes">Thời lượng buổi (phút) — null thì bỏ qua phần thời lượng.</param>
        Task SendInvitationEmailAsync(
            string toEmail,
            string campaignTitle,
            string joinLink,
            DateTime? expiresAt,
            DateTime? slotStartsAt,
            DateTime? slotEndsAt,
            DateTime? startsAt = null,
            string? orgName = null,
            bool faceVerifyEnabled = false,
            int? timeLimitMinutes = null,
            CancellationToken ct = default);
    }
}
