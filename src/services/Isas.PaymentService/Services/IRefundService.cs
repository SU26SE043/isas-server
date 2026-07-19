namespace Isas.PaymentService.Services
{
    /// <summary>
    /// F18 — hoàn tiền / đảo giao dịch (PlatformAdmin, AUTH-7).
    ///
    /// PHẠM VI: chỉ đơn <c>CreditPack</c> — loại đơn duy nhất từng cộng credit vào ví, nên cũng là loại
    /// duy nhất có bút toán để đảo. Đơn <c>InvoiceSettlement</c> và <c>Subscription*</c> trả về
    /// <see cref="RefundOutcome.UnsupportedKind"/>: hoàn một hoá đơn postpaid nghĩa là mở lại kỳ tính
    /// cước đã chốt (và <c>InvoiceStatus.Void</c> mới là trạng thái đúng cho nó), còn hoàn một kỳ thuê
    /// bao nghĩa là thu hồi quyền dùng giữa kỳ — cả hai là nghiệp vụ riêng, không phải "đảo bút toán".
    /// Làm bừa ở đây sẽ đẻ ra đúng loại lỗi tiền mà vòng S8 vừa bịt.
    /// </summary>
    public interface IRefundService
    {
        /// <param name="adminUserId">`sub` của admin thực hiện — ghi vào <c>orders.refunded_by</c>.</param>
        /// <param name="allowPartialClawback">
        /// Cho phép tiếp tục khi ví không còn đủ credit để thu hồi trọn khoản đã bán. Mặc định <c>false</c>
        /// (dừng lại và báo số) — mặc định phải là "hỏi người", vì thu hồi thiếu là mất tiền thật của
        /// công ty và không ai khác trong hệ thống phát hiện hộ.
        /// </param>
        Task<RefundResult> RefundOrderAsync(
            Guid orderId,
            Guid adminUserId,
            string? reason,
            string? gatewayRef,
            bool allowPartialClawback,
            CancellationToken ct = default);
    }
}
