using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

public class StuckAnswerRepublisher : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);

    // Uploaded mà chưa publish lần nào (LastScoringPublishedAt == null) quá ngưỡng này
    // = publish hụt lúc upload -> đẩy lại sớm. Đo theo CreatedAt để chừa cửa sổ
    // upload đang dở (request còn chạy, status chưa kịp thành Scoring).
    private static readonly TimeSpan PublishFailedThreshold = TimeSpan.FromMinutes(2);

    // Đã publish nhưng quá lâu không thấy callback = worker mất tích (crash/mất message)
    // -> đẩy lại. Để dài để không đua với worker đang chấm chậm. Đo theo LastScoringPublishedAt.
    private static readonly TimeSpan ScoringLostThreshold = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScoringJobPublisher _publisher;  // singleton, inject thẳng được
    private readonly ILogger<StuckAnswerRepublisher> _logger;

    public StuckAnswerRepublisher(
        IServiceScopeFactory scopeFactory,
        IScoringJobPublisher publisher,
        ILogger<StuckAnswerRepublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
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
                _logger.LogError(ex, "Lỗi khi quét answer kẹt");
            }

            await Task.Delay(ScanInterval, ct);
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        // BackgroundService là singleton -> phải tạo scope riêng cho DbContext (scoped).
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();

        var now = DateTime.UtcNow;
        var publishGrace = now - PublishFailedThreshold;  // cho upload kịp publish
        var scoringCutoff = now - ScoringLostThreshold;   // mốc coi là worker mất tích

        // Answer cần (re)publish: thuộc session InProgress/Scoring, đã có audio, và:
        //  - publish hụt: chưa publish lần nào (null) và upload đã quá grace, HOẶC
        //  - kẹt thật: đã publish nhưng quá lâu (LastScoringPublishedAt < cutoff).
        var stuck = await db.PracticeAnswers
            .Where(a => a.AudioObjectKey != null
                        && (a.Session.Status == SessionStatus.InProgress
                            || a.Session.Status == SessionStatus.Scoring)
                        && (a.Status == AnswerStatus.Uploaded
                            || a.Status == AnswerStatus.Scoring)
                        && (
                            (a.LastScoringPublishedAt == null && a.CreatedAt < publishGrace)
                            || (a.LastScoringPublishedAt != null && a.LastScoringPublishedAt < scoringCutoff)
                        ))
            .Select(a => new
            {
                a.Id,
                a.SessionId,
                a.QuestionId,
                a.AudioObjectKey,
                JobCategory = a.Session.JobCategory,
                QuestionContent = a.Question.Content
            })
            .ToListAsync(ct);

        if (stuck.Count == 0) return;

        _logger.LogWarning("Phát hiện {Count} answer kẹt Uploaded, đang re-publish", stuck.Count);

        foreach (var a in stuck)
        {
            // Lấy rubric active của nghề (giống AnswerService).
            var criteria = await db.RubricCriteria
                .AsNoTracking()
                .Where(c => c.JobCategory == a.JobCategory && c.IsActive)
                .ToListAsync(ct);

            if (criteria.Count == 0)
            {
                _logger.LogWarning(
                    "Không có rubric active cho {JobCategory}, bỏ qua answer {AnswerId}",
                    a.JobCategory, a.Id);
                continue;
            }

            var job = new ScoringJob
            {
                AnswerId = a.Id,
                SessionId = a.SessionId,
                QuestionId = a.QuestionId,
                AudioObjectKey = a.AudioObjectKey!,
                QuestionContent = a.QuestionContent,
                JobCategory = a.JobCategory.ToString(),
                RubricVersion = criteria[0].Version,
                Criteria = criteria.Select(c => new ScoringCriterionDto
                {
                    CriterionId = c.Id,
                    Name = c.Name,
                    Description = c.Description,
                    MaxScore = c.MaxScore,
                    Weight = c.Weight
                }).ToList()
            };

            try
            {
                await _publisher.PublishAsync(job, ct);

                // Đẩy lại OK -> Scoring + dời mốc publish sang now, để vòng quét sau
                // không nhặt lại trong vòng ScoringLostThreshold. ExecuteUpdate vì
                // ở đây dùng projection (không track entity).
                await db.PracticeAnswers
                    .Where(x => x.Id == a.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, AnswerStatus.Scoring)
                        .SetProperty(x => x.LastScoringPublishedAt, now), ct);

                _logger.LogInformation("Re-published answer {AnswerId}", a.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Re-publish thất bại answer {AnswerId}, để vòng sau", a.Id);
            }
        }
    }
}