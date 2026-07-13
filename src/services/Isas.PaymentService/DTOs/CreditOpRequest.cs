using System.Text.Json.Serialization;
using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    /// <summary>
    /// Body cho /internal/credits/reserve|consume|release (payment.md §Schemas). owner_id/session_id
    /// = Guid lỏng, không FK xuyên service (GEN-2). session_id = khoá idempotency (PAY-4).
    /// </summary>
    public class CreditOpRequest
    {
        // Chấp nhận string ("Org"/"User") lẫn số — internal caller (Interview/Campaign) serialize enum string.
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public OwnerType OwnerType { get; set; }
        public Guid OwnerId { get; set; }
        public Guid SessionId { get; set; }
    }

    /// <summary>Res 200 của /internal/credits/reserve.</summary>
    public class ReserveResponse
    {
        public Guid ReservationId { get; set; }
        public int ReservedCredits { get; set; }
    }
}
