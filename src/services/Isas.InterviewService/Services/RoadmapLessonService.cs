using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

    private readonly ScoringOptions _scoring;   // F6a — ngưỡng "điểm yếu" (dùng chung với BC9)

    public RoadmapLessonService(
        InterviewDbContext db,
        IPracticeService practiceService,
        IAiServiceRoadmapGenerator generator,
        ILogger<RoadmapLessonService> logger,
        // Optional (default null) → test cũ dựng 4 tham số vẫn compile; DI inject bản thật.
        IOptions<ScoringOptions>? scoringOptions = null)
    {
        _db = db;
        _practiceService = practiceService;
        _generator = generator;
        _logger = logger;
        _scoring = scoringOptions?.Value ?? new ScoringOptions();
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
        // RAG grounding (Cách 2) — feed snapshot precompute (lesson.GroundingRefs) → AI cite trong tập đó.
        var focus = lesson.Milestone.FocusCriteria ?? new List<string>();
        var generated = await _generator.GenerateLessonTheoryAsync(
            roadmap.JobCategory.ToString(), roadmap.Level.ToString(),
            lesson.Title, focus, BuildWeaknesses(roadmap, focus),
            grounding: lesson.GroundingRefs, ct: ct);
        var theory = generated.TheoryMarkdown;
        // F15 — tài liệu học sinh CÙNG lượt với lý thuyết; lưu chung 1 lần ghi để không có trạng
        // thái "có theory mà chưa có resources" (guard idempotent bên dưới chỉ nhìn theory_content).
        var resources = generated.Resources.ToList();
        var now = DateTime.UtcNow;

        // RAG grounding — NARROW snapshot precompute về đúng chunk AI THẬT SỰ cite (guard over-attribution +
        // drop by-construction: .Where trên lesson.GroundingRefs ⇒ chỉ giữ chunk vừa nằm trong tập cấp vừa
        // được cite; id lạ AI bịa tự rơi). 3 trạng thái: precompute chưa chạy (null) → null; đã chạy nhưng
        // AI không cite / corpus rỗng → [] (ungrounded); có cite → non-empty (grounded).
        var citedRefs = NarrowToCited(lesson.GroundingRefs, generated.CitedChunkIds);

        // Lưu idempotent: chỉ ghi khi theory_content vẫn null (2 request đồng thời → chỉ 1 ghi thắng).
        var updated = await _db.RoadmapLessons
            .Where(l => l.Id == lessonId && l.TheoryContent == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.TheoryContent, theory)
                .SetProperty(l => l.Resources, resources)
                .SetProperty(l => l.GroundingRefs, citedRefs)
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
        lesson.Resources = resources;
        lesson.GroundingRefs = citedRefs;
        lesson.TheoryGeneratedAt = now;
        return MapLesson(lesson);
    }

    // RAG grounding — narrow snapshot precompute về đúng chunk được cite. null (chưa precompute) → null;
    // đã precompute nhưng không cite → [] (ungrounded); có cite → subset. .Where trên tập cấp ⇒ id lạ tự rơi.
    private static List<GroundingChunk>? NarrowToCited(
        IReadOnlyList<GroundingChunk>? provided, IReadOnlyList<string>? citedChunkIds)
    {
        if (provided is null) return null;
        if (citedChunkIds is not { Count: > 0 }) return new List<GroundingChunk>();
        var cited = new HashSet<string>(citedChunkIds, StringComparer.Ordinal);
        return provided.Where(g => cited.Contains(g.ChunkId)).ToList();
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

    /// <summary>
    /// F6a — điểm yếu THẬT của ứng viên ở đúng các tiêu chí mà bài học này nhắm tới.
    ///
    /// Trước đây luôn truyền `weaknesses: null`, nên nhánh `if weaknesses:` trong prompt AIService
    /// (prompts.py) là code CHẾT: đường ống đã thông từ interface tới prompt, chỉ thiếu mỗi dữ liệu.
    /// Hệ quả: bài học viết chung chung, không bám vào chỗ ứng viên đang yếu.
    ///
    /// Nguồn = `roadmap.Baseline` (tên tiêu chí → % lúc lập roadmap), vốn ĐÃ nằm sẵn trong entity đã
    /// `.Include()` ở LoadOwnedLessonAsync ⇒ 0 query thêm. Cố ý KHÔNG query `session_criterion_scores`
    /// cho tươi hơn: chính xác hơn chút nhưng tốn thêm 1 query trên đường lazy-gen vốn đã phải chờ AI
    /// đồng bộ, mà Baseline chính là snapshot của cùng dữ liệu đó.
    ///
    /// Giao với FocusCriteria để không "mách" AI những điểm yếu lạc đề với bài học đang mở.
    /// Rỗng → null (giữ nguyên hành vi cũ, prompt bỏ qua nhánh này).
    /// </summary>
    private List<string>? BuildWeaknesses(Roadmap roadmap, IReadOnlyList<string> focus)
    {
        if (roadmap.Baseline is not { Count: > 0 } baseline || focus.Count == 0)
            return null;

        var weaknesses = focus
            .Where(name => baseline.TryGetValue(name, out var pct)
                           && pct < _scoring.ImprovementThresholdPct)
            .Select(name => $"{name}: {baseline[name]:0.#}%")
            .ToList();

        return weaknesses.Count > 0 ? weaknesses : null;
    }

    private static LessonResponse MapLesson(RoadmapLesson l)
        => new(l.Id, l.OrderNo, l.Title, l.TheoryContent, l.SessionId, l.Status.ToString(),
               (l.Resources ?? []).Select(MapResource).ToList(),
               // RAG grounding — nguồn AI đã cite (narrow ở OpenLessonAsync). null = chưa precompute.
               GroundingMapper.ToCitations(l.GroundingRefs));

    /// <summary>F15 — entity → DTO. Dùng chung với <see cref="RoadmapService"/> để 2 đường trả
    /// cùng shape (chi tiết lesson vs roadmap detail).</summary>
    internal static LessonResourceResponse MapResource(LessonResource r)
        => new(r.Title, r.Type, r.Publisher, r.Url);
}
