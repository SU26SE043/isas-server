namespace Isas.InterviewService.DTOs;

// BC2 — kết quả reserve credit ví cá nhân (Payment /internal/credits/reserve trả về).
// reservationId/reservedCredits chỉ để log/trace; consume/release (BC3/BC4) khoá theo sessionId nên
// Interview KHÔNG cần lưu reservationId (idempotency ở phía Payment theo session_id).
public record CreditReservationResult(Guid ReservationId, int ReservedCredits);
