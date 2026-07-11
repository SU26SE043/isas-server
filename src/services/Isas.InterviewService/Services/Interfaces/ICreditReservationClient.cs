using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// BC2 — client nội bộ gọi PaymentService `/internal/credits/reserve|consume|release` (máy-máy,
// X-Internal-Token, KHÔNG qua gateway). Chỉ B2C (owner=User): giữ/trừ/hoàn credit ví cá nhân.
// Ví hết credit/hạn mức → Payment 402 → InsufficientCreditException (→ KHÔNG tạo row, PAY-5).
// Lỗi hạ tầng (Payment down / 5xx / JSON hỏng) → PaymentServiceException (→ 502).
public interface ICreditReservationClient
{
    // Idempotent theo sessionId (P4) → an toàn retry. sessionId = khoá reservation (session hoặc
    // op không-session như phân tích CV BC7b — caller cấp Guid làm khoá).
    Task<CreditReservationResult> ReserveAsync(
        string ownerType, Guid ownerId, Guid sessionId, CancellationToken ct = default);

    // BC7b — trừ credit thật khi op thành công (reservation Reserved→Consumed). Idempotent/absorbing
    // theo operationId (PAY-11): gọi lại / miss reserve → Payment no-op 200.
    Task ConsumeAsync(Guid operationId, CancellationToken ct = default);

    // BC7b — hoàn chỗ giữ khi op lỗi (reservation Reserved→Released, không trừ credit).
    // Idempotent/absorbing theo operationId (PAY-11): gọi lại / đã Consumed / miss reserve → no-op 200.
    Task ReleaseAsync(Guid operationId, CancellationToken ct = default);
}
