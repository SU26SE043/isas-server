namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Bọc kênh CHI payOS (<c>/v1/payouts</c>) — mẫu <see cref="IPayOsQueryClient"/>: tách abstraction
    /// để test chạy trên SQLite không cần payOS thật, và để chỗ nối duy nhất với tiền-đi-ra nằm sau một
    /// interface mock được.
    ///
    /// <para><b>Không tự ký HMAC.</b> SDK payOS 2.1.0 lo chữ ký. Điều này đáng nói vì chữ ký của
    /// <c>payouts</c> KHÁC <c>payment-requests</c> (payouts URL-encode từng giá trị) — tự ký là tự rước
    /// một lớp lỗi im lặng mà SDK đã giải.</para>
    /// </summary>
    public interface IPayoutClient
    {
        /// <summary>Credential kênh chi đã cấu hình chưa. Chưa → không endpoint nào được phép gọi chi.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Tạo lệnh chi. <paramref name="idempotencyKey"/> do CALLER cấp và phải là khoá đã được ghi bền
        /// vững trước khi gọi — SDK truyền thẳng nó lên header <c>x-idempotency-key</c>, nên gọi lại với
        /// cùng khoá là an toàn, còn gọi lại với khoá mới là chuyển tiền lần hai.
        /// </summary>
        /// <param name="referenceId">Mã tham chiếu phía ta (dùng order id) — payOS echo lại để đối soát.</param>
        Task<PayoutCreateResult> CreateAsync(
            string referenceId,
            long amountVnd,
            string description,
            string toBin,
            string toAccountNumber,
            Guid idempotencyKey,
            CancellationToken ct = default);

        /// <summary>Đọc trạng thái một lệnh chi. <c>null</c> = không tra được (không suy ra thành công/thất bại).</summary>
        Task<PayoutSnapshot?> GetAsync(string payoutId, CancellationToken ct = default);

        /// <summary>
        /// Số dư ví chi. <c>null</c> = không đọc được — caller phải coi đó là "không biết" chứ không
        /// phải "bằng 0", và cũng không được lấy nó làm cớ chặn/cho phép chi.
        /// </summary>
        Task<long?> GetBalanceAsync(CancellationToken ct = default);
    }
}
