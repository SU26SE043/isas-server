namespace Isas.InterviewService.Services.Interfaces;

// DB2 — settlement-event qua Transactional Outbox. Tách 2 trách nhiệm để CALLER giữ quyền commit
// (đóng session state + outbox-row CÙNG 1 SaveChanges — atomic):
//   • Enqueue* : tính event + `db.OutboxMessages.Add(row)` (KHÔNG save) — gọi TRƯỚC SaveChanges của caller.
//   • Notify*  : side-effect best-effort SAU khi đã commit (BC9 tổng kết / BC10 nhận xét / BC14/15 roadmap).
// Dùng chung 2 nơi đóng session Scored/Abandoned: AnswerService (callback chấm dần) + PracticeService
// (nhánh đóng-ngay submit). Publish thật do OutboxDispatcher lo (đường duy nhất, tránh double-publish).
public interface ISessionScoringNotifier
{
    // Tính điểm tổng (weighted) + build SessionScoredEvent + ghi outbox-row. KHÔNG SaveChanges (caller
    // commit chung với state-flip). Áp cả B2B (TotalScore weighted ranking) & B2C.
    Task EnqueueSessionScoredAsync(Guid sessionId, CancellationToken ct = default);

    // PAY-13 / BK12: session đóng mà KHÔNG có answer nào Scored (mọi answer Failed/Skipped) hoặc sinh câu
    // hỏi lỗi (generation_failed) → build SessionAbandonedEvent + ghi outbox-row (Payment RELEASE thay vì
    // consume). KHÔNG SaveChanges (caller commit chung với state-flip).
    Task EnqueueSessionAbandonedAsync(Guid sessionId, string reason, CancellationToken ct = default);

    // Side-effect best-effort SAU khi session đã đóng Scored (đã commit): BC9 tổng kết điểm B2C, BC10
    // nhận xét AI, BC14 lesson Done, BC15 roadmap report. Lỗi KHÔNG chặn (session đã Scored trong DB).
    Task NotifySessionScoredAsync(Guid sessionId, CancellationToken ct = default);
}
