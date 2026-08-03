namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Kết cục của lời gọi TẠO lệnh chi. Ba nhánh đầu là câu trả lời dứt khoát của payOS;
    /// <see cref="Unknown"/> là "không có câu trả lời" và phải được xử lý KHÁC HẲN thất bại.
    /// </summary>
    public enum PayoutCallOutcome
    {
        /// <summary>payOS nhận lệnh mới.</summary>
        Created,

        /// <summary>
        /// Khoá idempotency đã được dùng ⇒ lệnh này ĐÃ tồn tại từ trước. Đây là bằng chứng DƯƠNG rằng
        /// lệnh đã vào hệ thống payOS, KHÔNG phải lỗi — và tuyệt đối không được thử lại bằng khoá mới.
        /// </summary>
        AlreadyExists,

        /// <summary>
        /// payOS từ chối dứt khoát (đích không hợp lệ, ví chi không đủ, cấu hình sai...). Tiền CHƯA đi.
        /// </summary>
        Rejected,

        /// <summary>
        /// Timeout / mất mạng / lỗi không phân loại được ⇒ <b>không biết</b> lệnh đã vào hay chưa.
        /// KHÔNG được coi là thất bại: coi là thất bại rồi tạo lệnh mới chính là đường chuyển tiền hai lần.
        /// </summary>
        Unknown
    }

    /// <summary>Trạng thái lệnh chi nhìn từ payOS, đã gộp về đúng 3 nhánh ta cần hành động.</summary>
    public enum PayoutState
    {
        /// <summary>payOS nhận rồi nhưng CHƯA xong (Received hoặc Processing) — chưa được đóng dấu gì.</summary>
        InFlight,
        /// <summary>Đã chuyển xong.</summary>
        Succeeded,
        /// <summary>Hỏng dứt điểm (Failed/Cancelled).</summary>
        Failed
    }

    /// <summary>Thông tin một lệnh chi đọc về từ payOS.</summary>
    /// <param name="ToAccountName">
    /// Tên chủ tài khoản đích do NGÂN HÀNG trả về. Đây là thứ dùng để đối chiếu với tên người đã trả
    /// tiền: khớp id ngân hàng nhưng lệch tên nghĩa là sắp chuyển đúng lệnh cho nhầm người.
    /// </param>
    public sealed record PayoutSnapshot(
        PayoutState State,
        string? PayoutId,
        string? ToAccountName,
        string? Message);

    /// <summary>Kết quả gọi tạo lệnh chi.</summary>
    public sealed record PayoutCreateResult(
        PayoutCallOutcome Outcome,
        PayoutSnapshot? Payout,
        string? Message)
    {
        public static PayoutCreateResult Simple(PayoutCallOutcome outcome, string? message = null) =>
            new(outcome, null, message);
    }

    /// <summary>Kết cục một lệnh "chi tiền hoàn tự động" nhìn từ nghiệp vụ (map sang HTTP ở controller).</summary>
    public enum RefundPayoutOutcome
    {
        /// <summary>Đã gửi lệnh, đang chờ ngân hàng xử lý. Reconciler sẽ theo tiếp.</summary>
        InFlight,

        /// <summary>Tiền đã tới khách và đơn đã được đóng dấu <c>refund_settled_at</c>.</summary>
        Settled,

        /// <summary>Đơn đã được đóng dấu chuyển tiền từ trước — không chuyển lại (idempotent).</summary>
        AlreadySettled,

        /// <summary>Không tìm thấy đơn.</summary>
        OrderNotFound,

        /// <summary>Đơn chưa <c>Refunded</c> — chưa quyết định hoàn thì chưa có gì để chuyển.</summary>
        NotRefunded,

        /// <summary>Tính năng chưa bật, hoặc chưa cấu hình credential kênh chi.</summary>
        NotEnabled,

        /// <summary>
        /// Không dựng được đích chuyển: webhook gốc thiếu số tài khoản, hoặc mã ngân hàng không đổi được
        /// sang BIN. Admin chuyển tay.
        /// </summary>
        DestinationUnresolved,

        /// <summary>Vượt trần chi tự động — buộc chuyển tay để có người nhìn con số.</summary>
        OverCeiling,

        /// <summary>Ví chi không đủ số dư (đọc được và nhỏ hơn số cần chi).</summary>
        InsufficientBalance,

        /// <summary>payOS từ chối dứt khoát — tiền CHƯA đi.</summary>
        Rejected,

        /// <summary>
        /// Tiền ĐÃ đi nhưng tên chủ tài khoản nhận không khớp người đã trả ⇒ nhiều khả năng chuyển nhầm.
        /// KHÔNG đóng dấu settle: đóng dấu nghĩa là khẳng định khách đã nhận được tiền hoàn.
        /// </summary>
        NameMismatch
    }

    /// <summary>Kết quả một lệnh chi tiền hoàn.</summary>
    public sealed record RefundPayoutResult(
        RefundPayoutOutcome Outcome,
        Guid OrderId,
        string? PayoutId = null,
        DateTime? RefundSettledAt = null,
        string? Message = null);
}
