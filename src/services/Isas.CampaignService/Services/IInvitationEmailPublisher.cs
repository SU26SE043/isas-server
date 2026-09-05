namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Job đẩy vào email queue cho 1 lời mời (D1). Worker gửi mail thật KHÔNG thuộc phạm vi D1.
    ///
    /// <para>CMP1-B4 — 4 trường cuối để thư mời NÓI ĐỦ cho ứng viên chuẩn bị: giờ chiến dịch MỞ
    /// (<c>StartsAt</c>, khác <c>ExpiresAt</c> = hạn lời mời), tên công ty mời (<c>OrgName</c>, resolve
    /// TẠI ĐÂY — lúc tạo job — KHÔNG resolve lại ở consumer, để payload tự chứa đủ), có bắt buộc
    /// camera/mic không (<c>FaceVerifyEnabled</c>), và thời lượng buổi (<c>TimeLimitMinutes</c>).
    /// Optional với default <c>null/null/false/null</c> = "không biết gì thêm" ⇒ job cũ trong outbox
    /// (viết trước bản này) deserialize vẫn ra đúng "không có thông tin mới", KHÔNG ném.</para>
    ///
    /// <para>⚠ Đây là chặng dây DỄ RỤNG NHẤT của cả tính năng: thiếu một trường ở nơi <c>new
    /// InvitationEmailJob(...)</c> được gọi (3 call-site trong <c>CampaignService</c>) thì THƯ VẪN
    /// GỬI — chỉ thiếu chữ, không exception, không log, không ai biết trừ khi đọc đúng thư đó.</para>
    /// </summary>
    public record InvitationEmailJob(
        Guid InvitationId,
        Guid CampaignId,
        string Email,
        string Token,
        string CampaignTitle,
        DateTime? ExpiresAt,
        DateTime? StartsAt = null,
        string? OrgName = null,
        bool FaceVerifyEnabled = false,
        int? TimeLimitMinutes = null);

    public interface IInvitationEmailPublisher
    {
        Task PublishAsync(InvitationEmailJob job, CancellationToken ct = default);

        // DB2b — OutboxDispatcher publish payload NGUYÊN từ outbox-row (không reconstruct job). messageId
        // → BasicProperties.MessageId. Lỗi (broker down) → ném ra để dispatcher giữ published_at null + Attempts++.
        Task PublishRawAsync(string payloadJson, string messageId, CancellationToken ct = default);
    }
}
