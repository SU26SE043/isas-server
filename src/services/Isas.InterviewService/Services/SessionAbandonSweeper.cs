using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

// E3: quét định kỳ session `InProgress` quá hạn mà KHÔNG có answer nào -> coi là bỏ ngang
// (interview.md §State machine + §Sự kiện phát ra). Đóng session sang `SessionAbandoned` + phát
// event cùng tên để Payment release reservation. Mirror pattern background-sweep của
// StuckAnswerRepublisher (ScanInterval 2', scope riêng cho DbContext).
//
// ⚠ Ngưỡng "quá hạn" TẠM DÙNG CreatedAt + hằng số cố định (giống StuckAnswerRepublisher). Doc đích
// (task I2, CHƯA build) dùng `campaigns.expires_at` thật (hạn chót nhận bài B2B) — nhưng cột đó
// nằm ở DB CampaignService, KHÔNG có trong DB Interview (no cross-service FK — GEN-2) nên chưa thể
// đọc; migration thêm cột deadline riêng ngoài phạm vi E3 (task không yêu cầu, dep "—"). Khi I2
// landing (vd Campaign gửi kèm expires_at lúc tạo session, hoặc field khác), thay ngưỡng này bằng
// giá trị thật.
//
// Nhánh ≥1 answer -> auto-submit khi quá hạn KHÔNG thuộc task này (I2) — sweeper chỉ xử lý case
// 0 answer. `InProgress` với 0 answer hiện KHÔNG xảy ra qua luồng hiện có (transition Ready ->
// InProgress chỉ xảy ra cùng lúc answer đầu tiên được lưu — AnswerService.UploadAnswerAsync) nên
// nhánh này tạm thời "chờ sẵn" cho các luồng tương lai (resume B2B, D2/I2) có thể tạo ra ca này.
public class SessionAbandonSweeper : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);

    // Session InProgress không có answer nào quá ngưỡng này (tính từ CreatedAt) -> bỏ ngang.
    private static readonly TimeSpan AbandonAfterThreshold = TimeSpan.FromMinutes(30);

    private const string AbandonReason = "expired_no_answer";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISessionEventPublisher _eventPublisher;
    private readonly ILogger<SessionAbandonSweeper> _logger;

    public SessionAbandonSweeper(
        IServiceScopeFactory scopeFactory,
        ISessionEventPublisher eventPublisher,
        ILogger<SessionAbandonSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Chờ 1 nhịp trước khi quét lần đầu, để app khởi động xong.
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(ct);
            }
            catch (Exception ex)
            {
                // Không để 1 vòng lỗi giết cả background service.
                _logger.LogError(ex, "Lỗi khi quét session bỏ ngang");
            }

            await Task.Delay(ScanInterval, ct);
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        // BackgroundService là singleton -> phải tạo scope riêng cho DbContext (scoped).
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();

        var cutoff = DateTime.UtcNow - AbandonAfterThreshold;

        // InProgress + quá hạn + KHÔNG có answer nào (0 answer -> bỏ ngang; ≥1 answer là I2).
        var stuck = await db.PracticeSessions
            .Where(s => s.Status == SessionStatus.InProgress
                        && s.CreatedAt < cutoff
                        && !db.PracticeAnswers.Any(a => a.SessionId == s.Id))
            .Select(s => new { s.Id, s.CampaignId, s.CandidateId })
            .ToListAsync(ct);

        if (stuck.Count == 0) return;

        _logger.LogWarning(
            "Phát hiện {Count} session InProgress quá hạn không có câu trả lời, đánh dấu bỏ ngang",
            stuck.Count);

        foreach (var s in stuck)
        {
            var now = DateTime.UtcNow;

            // Guard Status == InProgress trong WHERE để tránh race với vòng quét khác/luồng khác
            // đã đổi trạng thái session giữa lúc SELECT và UPDATE.
            var updated = await db.PracticeSessions
                .Where(x => x.Id == s.Id && x.Status == SessionStatus.InProgress)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.Status, SessionStatus.SessionAbandoned)
                    .SetProperty(x => x.CompletedAt, now), ct);

            if (updated == 0) continue;

            // BC14: session bỏ ngang mà đang gắn 1 roadmap lesson (Practicing) → trả lesson về Theory +
            // clear session_id để user /start lại được (session bỏ ngang không có điểm — mất link chấp
            // nhận được). Release credit do E7 lo qua event dưới (KHÔNG tự gọi Payment ở đây). Best-effort.
            try
            {
                await db.RoadmapLessons
                    .Where(l => l.SessionId == s.Id && l.Status == LessonStatus.Practicing)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(l => l.Status, LessonStatus.Theory)
                        .SetProperty(l => l.SessionId, (Guid?)null), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BC14: revert lesson về Theory thất bại cho session {SessionId}", s.Id);
            }

            var evt = new SessionAbandonedEvent
            {
                SessionId = s.Id,
                CampaignId = s.CampaignId,
                CandidateId = s.CandidateId,
                Reason = AbandonReason,
                AbandonedAt = now
            };

            try
            {
                await _eventPublisher.PublishSessionAbandonedAsync(evt, ct);
                _logger.LogInformation("Đã phát SessionAbandoned cho session {SessionId}", s.Id);
            }
            catch (Exception ex)
            {
                // Publish lỗi KHÔNG được làm hỏng việc đóng session — session đã Abandoned trong
                // DB rồi (giống pattern nuốt lỗi publish ở SessionScoringNotifier/E2). Miss event
                // ở đây tạm thời làm Payment lệch (chưa có backfill trong E3).
                _logger.LogError(ex, "Phát SessionAbandoned thất bại cho session {SessionId}", s.Id);
            }
        }
    }
}
