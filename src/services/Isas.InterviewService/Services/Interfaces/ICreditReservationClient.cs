using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// BC2 — client nội bộ gọi PaymentService `/internal/credits/reserve` (máy-máy, X-Internal-Token,
// KHÔNG qua gateway). Chỉ B2C (owner=User): giữ 1 chỗ credit ví cá nhân khi tạo session luyện.
// Ví hết credit/hạn mức → Payment 402 → InsufficientCreditException (→ KHÔNG tạo session, PAY-5).
// Lỗi hạ tầng (Payment down / 5xx / JSON hỏng) → PaymentServiceException (→ 502).
public interface ICreditReservationClient
{
    // Idempotent theo sessionId (P4) → an toàn retry.
    Task<CreditReservationResult> ReserveAsync(
        string ownerType, Guid ownerId, Guid sessionId, CancellationToken ct = default);
}
