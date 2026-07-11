namespace PaymentService.Models
{
    /// <summary>
    /// Giữ chỗ 1 credit theo session (Reserve → Consume → Release, D7). P1 chỉ tạo schema;
    /// việc ghi/chuyển trạng thái (Reserved/Consumed/Released) là P4/P5/P6.
    /// UNIQUE(session_id) = idempotency key — 1 reservation / session.
    /// </summary>
    public class CreditReservation
    {
        public Guid Id { get; set; }
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public Guid SessionId { get; set; }
        public ReservationStatus Status { get; set; } = ReservationStatus.Reserved;
        public DateTime CreatedAt { get; set; }
    }

    public enum ReservationStatus
    {
        Reserved,
        Consumed,
        Released
    }
}
