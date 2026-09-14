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
        int? TimeLimitMinutes = null,
        // CMP3-B4 — phân loại. null/vắng ("Invitation") = thư mời magic-link (mọi row cũ deserialize
        // vào đây, KHÔNG ném). "OpenedEarly" = thư báo chiến dịch mở sớm hơn giờ ghi trong thư mời
        // gốc: KHÔNG có <c>Token</c> (DB chỉ giữ hash), consumer gửi thư trỏ ứng viên về link cũ và
        // KHÔNG đụng cờ <c>email_sent_at</c> (đó là cờ của thư mời).
        string? Kind = null,
        // Mốc <c>start_at</c> CŨ (trước khi kéo về hiện tại) — để thư "mở sớm" nói được "đổi từ đâu".
        DateTime? PreviousStartsAt = null);

    public interface IInvitationEmailPublisher
    {
        Task PublishAsync(InvitationEmailJob job, CancellationToken ct = default);

        // DB2b — OutboxDispatcher publish payload NGUYÊN từ outbox-row (không reconstruct job). messageId
        // → BasicProperties.MessageId. Lỗi (broker down) → ném ra để dispatcher giữ published_at null + Attempts++.
        Task PublishRawAsync(string payloadJson, string messageId, CancellationToken ct = default);
    }
}
