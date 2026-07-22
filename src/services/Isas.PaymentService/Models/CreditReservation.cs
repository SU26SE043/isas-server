namespace PaymentService.Models
{
    /// <summary>
    /// Giữ chỗ 1 credit theo session (Reserve → Consume → Release, D7). P1 chỉ tạo schema;
    /// việc ghi/chuyển trạng thái (Reserved/Consumed/Released) là P4/P5/P6.
    /// UNIQUE(session_id) = idempotency key — 1 reservation / session.
    /// </summary>
    public class CreditReservation : IHasUpdatedAt
    {
        public Guid Id { get; set; }
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public Guid SessionId { get; set; }
        public ReservationStatus Status { get; set; } = ReservationStatus.Reserved;

        /// <summary>
        /// F8 — nguồn chi trả cho chỗ giữ này, CHỐT MỘT LẦN lúc reserve và KHÔNG BAO GIỜ đọc lại từ
        /// trạng thái thuê bao. Đây là thứ giữ cho sổ cái khỏi đúc credit từ hư không:
        /// <see cref="ReservationFunding.Subscription"/> KHÔNG trừ cột nào lúc reserve, nên nếu Consume/
        /// Release lại quyết định theo "hiện giờ còn thuê bao không" thì một thuê bao hết hạn giữa buổi
        /// sẽ khiến <c>ReleaseAsync</c> chạy nhánh prepaid <c>remaining+1</c> → SINH RA một credit trả
        /// tiền chưa từng được mua. Chốt tại nguồn ⇒ nghịch đảo luôn khớp chiều thuận.
        /// Đồng thời chính là cách hiện thực "không văng người đang thi" (PAY-12).
        /// </summary>
        public ReservationFunding FundedBy { get; set; } = ReservationFunding.Credit;

        /// <summary>
        /// F23/BK24 — snapshot <c>PaymentMode</c> CỦA VÍ tại thời điểm reserve, CHỐT MỘT LẦN và
        /// KHÔNG BAO GIỜ đọc lại từ <c>credit_accounts.payment_mode</c> hiện tại. Chỉ có ý nghĩa khi
        /// <see cref="FundedBy"/> = <see cref="ReservationFunding.Credit"/> (nhánh Subscription
        /// không trừ ví nên không đọc field này).
        ///
        /// Lý do bắt buộc: Consume/Release trước đây đọc lại <c>PaymentMode</c> HIỆN TẠI của ví.
        /// Nếu admin đổi Prepaid→Postpaid giữa lúc reservation đang <c>Reserved</c>: Release sẽ chạy
        /// nhánh postpaid (chỉ <c>reserved−1</c>, không hoàn <c>remaining</c>) dù credit đã bị trừ
        /// theo luật prepaid lúc reserve → MẤT credit khách. Ngược lại Postpaid→Prepaid giữa chừng
        /// sẽ ĐÚC credit (remaining+1) chưa từng được mua. Snapshot tại nguồn (mẫu <see cref="FundedBy"/>,
        /// F8) triệt tiêu cả hai. Default 'Prepaid' để mọi reservation CŨ (tạo trước F23, 100% ví khi đó
        /// đều Prepaid — đã verify DB thật 2026-07-20) giữ nguyên đúng hành vi.
        /// </summary>
        public PaymentMode PaymentMode { get; set; } = PaymentMode.Prepaid;

        public DateTime CreatedAt { get; set; }
        // DB14 — audit: đóng dấu khi status flip Reserved→Consumed/Released. Cả 2 flip dùng ExecuteUpdate
        // (CreditAccountService.Consume/Release) → tự thêm .SetProperty(UpdatedAt) ở đó. C# init cho insert.
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public enum ReservationStatus
    {
        Reserved,
        Consumed,
        Released
    }

    /// <summary>F8 — nguồn chi trả của một chỗ giữ (xem <see cref="CreditReservation.FundedBy"/>).</summary>
    public enum ReservationFunding
    {
        /// <summary>Trừ vào ví credit (prepaid remaining−1 / postpaid dồn nợ tới hạn mức). Mặc định = hành vi trước F8.</summary>
        Credit,

        /// <summary>
        /// Thuê bao còn hạn ⇒ KHÔNG trừ ví, KHÔNG ghi sổ cái. Row reservation vẫn được tạo để giữ nguyên
        /// idempotency theo session (PAY-4), tính hấp thụ Consumed/Released (PAY-11) và để
        /// <c>OrphanReservationReconciler</c> (DB18) vẫn dọn được chỗ giữ mồ côi.
        /// </summary>
        Subscription
    }
}
