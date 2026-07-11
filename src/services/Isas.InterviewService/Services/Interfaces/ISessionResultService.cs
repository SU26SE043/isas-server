namespace Isas.InterviewService.Services.Interfaces;

// BC9 — tính + ghi tổng kết điểm buổi luyện B2C khi session -> Scored.
public interface ISessionResultService
{
    // Tính overall_score + answered_count + breakdown session_criterion_scores cho 1 session B2C
    // và ghi DB (idempotent). No-op nếu session B2B (campaign_id có) hoặc không tồn tại.
    Task ComputeAndStoreAsync(Guid sessionId, CancellationToken ct = default);
}
