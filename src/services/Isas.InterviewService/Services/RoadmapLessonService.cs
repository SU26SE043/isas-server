using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

// BC14 (D20) — thao tác cấp lesson: mở lesson (lý thuyết lazy) + /start luyện. Owner-only.
// Lý thuyết sinh LẦN ĐẦU (lazy, idempotent) — miễn phí. /start = practice session B2C (reserve credit,
// tái dùng PracticeService.CreateLessonSessionAsync) rồi link lesson. Scored→Done / Abandoned→Theory
// móc ở luồng đóng session (SessionScoringNotifier / SessionAbandonSweeper) — KHÔNG ở đây.
public class RoadmapLessonService : IRoadmapLessonService
{
    private readonly InterviewDbContext _db;
    private readonly IPracticeService _practiceService;
    private readonly IAiServiceRoadmapGenerator _generator;
    private readonly ILogger<RoadmapLessonService> _logger;

    public RoadmapLessonService(
        InterviewDbContext db,
        IPracticeService practiceService,
        IAiServiceRoadmapGenerator generator,
        ILogger<RoadmapLessonService> logger)
    {
        _db = db;
        _practiceService = practiceService;
        _generator = generator;
        _logger = logger;
    }

    public async Task<LessonResponse> OpenLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct = default)
    {
        var lesson = await LoadOwnedLessonAsync(candidateId, roadmapId, lessonId, ct);
        var roadmap = lesson.Milestone.Roadmap;

        // Đã có lý thuyết → đọc DB, KHÔNG gọi AI lần 2 (lazy, idempotent).
        if (!string.IsNullOrEmpty(lesson.TheoryContent))
            return MapLesson(lesson);

        // Lazy-gen: gọi AIService (sync). Lỗi → AiServiceException (502) → chưa lưu gì (mở lại được).
        var focus = lesson.Milestone.FocusCriteria ?? new List<string>();
        var theory = await _generator.GenerateLessonTheoryAsync(
            roadmap.JobCategory.ToString(), roadmap.Level.ToString(),
            lesson.Title, focus, weaknesses: null, ct);
        var now = DateTime.UtcNow;

        // Lưu idempotent: chỉ ghi khi theory_content vẫn null (2 request đồng thời → chỉ 1 ghi thắng).
        var updated = await _db.RoadmapLessons
            .Where(l => l.Id == lessonId && l.TheoryContent == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.TheoryContent, theory)
                .SetProperty(l => l.TheoryGeneratedAt, now), ct);

        if (updated == 0)
        {
            // Request khác vừa sinh xong trước → trả bản đã lưu (không ghi đè).
            var fresh = await _db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lessonId, ct);
            return MapLesson(fresh);
        }

        _logger.LogInformation("BC14: sinh lý thuyết lesson {LessonId} (roadmap {RoadmapId})", lessonId, roadmapId);

        // Trả bản vừa sinh (khỏi round-trip). lesson đang detached (AsNoTracking) → set để dựng response.
        lesson.TheoryContent = theory;
        lesson.TheoryGeneratedAt = now;
        return MapLesson(lesson);
    }

    public async Task<PracticeSessionResponse> StartLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct = default)
    {
        var lesson = await LoadOwnedLessonAsync(candidateId, roadmapId, lessonId, ct);
        var roadmap = lesson.Milestone.Roadmap;

        // Đang luyện / đã xong → 409 (resume session cũ, KHÔNG reserve thêm credit).
        if (lesson.Status == LessonStatus.Practicing)
            throw new LessonAlreadyStartedException("Lesson đang luyện — tiếp tục buổi hiện tại.", lesson.SessionId);
        if (lesson.Status == LessonStatus.Done)
            throw new LessonAlreadyStartedException("Lesson đã hoàn thành.", lesson.SessionId);

        // Practice session B2C: reserve 1 credit (hết → 402 KHÔNG tạo session), câu hỏi bám focusCriteria.
        // sessionId cấp trước để link lesson SAU khi session tồn tại (thoả FK roadmap_lessons.session_id).
        // Reserve/gen lỗi → CreateLessonSessionAsync ném (402/gen-fail) TRƯỚC khi link ⇒ lesson vẫn Theory.
        var sessionId = Guid.NewGuid();
        var req = new CreatePracticeSessionRequest(roadmap.CvId, JdId: null, roadmap.JobCategory);
        var response = await _practiceService.CreateLessonSessionAsync(
            candidateId, req, sessionId, lesson.Milestone.FocusCriteria, ct);

        // Link atomic (guard Status==Theory chống double-start): chỉ khi còn Theory mới set Practicing +
        // session_id. Đua 2 /start cùng lúc → chỉ 1 thắng; kẻ thua để lại session mồ côi (rất hiếm, cùng
        // 1 user) — credit sẽ được E7 hoàn khi session đó bỏ ngang/hết hạn.
        var linked = await _db.RoadmapLessons
            .Where(l => l.Id == lessonId && l.Status == LessonStatus.Theory)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, LessonStatus.Practicing)
                .SetProperty(l => l.SessionId, sessionId), ct);

        if (linked == 0)
        {
            _logger.LogWarning(
                "BC14: lesson {LessonId} bị /start đồng thời — session {SessionId} không link được (mồ côi)",
                lessonId, sessionId);
            throw new LessonAlreadyStartedException("Lesson vừa được bắt đầu ở một yêu cầu khác.", null);
        }

        // Milestone Pending→InProgress khi lesson đầu tiên của mile được /start (idempotent — lesson kế no-op).
        await _db.RoadmapMilestones
            .Where(m => m.Id == lesson.MilestoneId && m.Status == MilestoneStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, MilestoneStatus.InProgress), ct);

        _logger.LogInformation(
            "BC14: /start lesson {LessonId} (roadmap {RoadmapId}) -> session {SessionId} Practicing",
            lessonId, roadmapId, sessionId);

        return response;
    }

    // Đọc lesson kèm milestone + roadmap (AsNoTracking). null → 404; roadmap khác chủ → 403.
    private async Task<RoadmapLesson> LoadOwnedLessonAsync(
        Guid candidateId, Guid roadmapId, Guid lessonId, CancellationToken ct)
    {
        var lesson = await _db.RoadmapLessons.AsNoTracking()
            .Include(l => l.Milestone).ThenInclude(m => m.Roadmap)
            .FirstOrDefaultAsync(l => l.Id == lessonId && l.Milestone.RoadmapId == roadmapId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy lesson này");

        if (lesson.Milestone.Roadmap.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải roadmap của bạn");

        return lesson;
    }

    private static LessonResponse MapLesson(RoadmapLesson l)
        => new(l.Id, l.OrderNo, l.Title, l.TheoryContent, l.SessionId, l.Status.ToString());
}
