using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

// BC12 (D20) — roadmap ôn tập cá nhân hoá B2C. Gom điểm yếu (session_criterion_scores, BC9) + CV
// → AIService /generate-roadmap (sync) → LƯU 3 bảng (AI KHÔNG ghi DB). Tạo roadmap KHÔNG trừ credit
// (D7/D15 — chỉ session luyện BC14 mới reserve). AI lỗi → 502, KHÔNG lưu gì (gọi AI trước khi Add).
public class RoadmapService : IRoadmapService
{
    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceRoadmapGenerator _generator;
    private readonly ILogger<RoadmapService> _logger;

    public RoadmapService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceRoadmapGenerator generator,
        ILogger<RoadmapService> logger)
    {
        _db = db;
        _storage = storage;
        _generator = generator;
        _logger = logger;
    }

    public async Task<RoadmapResponse> CreateAsync(
        Guid candidateId, CreateRoadmapRequest req, CancellationToken ct = default)
    {
        // CV optional — đọc parsed_text (kiểm chủ sở hữu). null → 404; khác chủ → 403; rỗng → 400.
        string? cvText = null;
        if (req.CvId is not null)
            cvText = await ReadOwnedParsedTextAsync(req.CvId.Value, candidateId, "CV", ct);

        // Gom điểm yếu + baseline từ các buổi B2C đã Scored (mới nhất trước).
        var scored = await _db.PracticeSessions.AsNoTracking()
            .Include(s => s.CriterionScores)
            .Where(s => s.CandidateId == candidateId
                        && s.CampaignId == null
                        && s.Status == SessionStatus.Scored)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        Dictionary<string, decimal>? baseline = null;
        List<RoadmapWeakness>? weaknesses = null;
        List<Guid>? sourceSessionIds = null;

        var withScores = scored.Where(s => s.CriterionScores.Count > 0).ToList();
        if (withScores.Count > 0)
        {
            // Newest-first: tiêu chí xuất hiện lần đầu (buổi mới nhất) thắng → baseline = % hiện tại.
            baseline = new Dictionary<string, decimal>();
            var weak = new List<RoadmapWeakness>();
            foreach (var s in withScores)
                foreach (var cs in s.CriterionScores)
                {
                    if (baseline.ContainsKey(cs.CriterionName)) continue;
                    baseline[cs.CriterionName] = cs.Percentage;
                    if (cs.NeedsImprovement)
                        weak.Add(new RoadmapWeakness(cs.CriterionName, cs.Percentage));
                }

            sourceSessionIds = withScores.Select(s => s.Id).ToList();
            weaknesses = weak.Count > 0 ? weak : null;
        }

        // Gọi AIService sinh cấu trúc (sync). Lỗi → AiServiceException (502) → KHÔNG lưu gì.
        var ai = await _generator.GenerateAsync(
            req.JobCategory.ToString(), req.Level.ToString(), weaknesses, cvText, ct);

        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            JobCategory = req.JobCategory,
            Level = req.Level,
            CvId = req.CvId,
            SourceSessionIds = sourceSessionIds,
            Baseline = baseline,
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

        var milestoneOrder = 1;
        foreach (var m in ai.Milestones)
        {
            var milestone = new RoadmapMilestone
            {
                Id = Guid.NewGuid(),
                OrderNo = milestoneOrder++,
                Title = m.Title,
                FocusCriteria = m.FocusCriteria.ToList(),
                Status = MilestoneStatus.Pending
            };

            var lessonOrder = 1;
            foreach (var l in m.Lessons)
                milestone.Lessons.Add(new RoadmapLesson
                {
                    Id = Guid.NewGuid(),
                    OrderNo = lessonOrder++,
                    Title = l.Title,
                    Status = LessonStatus.Theory,
                    TheoryContent = null
                });

            roadmap.Milestones.Add(milestone);
        }

        _db.Set<Roadmap>().Add(roadmap);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "BC12: roadmap {Id} candidate {CandidateId} ({Cat}/{Level}) milestones={M} sources={S}",
            roadmap.Id, candidateId, req.JobCategory, req.Level,
            roadmap.Milestones.Count, sourceSessionIds?.Count ?? 0);

        return Map(roadmap, includeTheory: true);
    }

    public async Task<RoadmapResponse?> GetAsync(
        Guid candidateId, Guid id, CancellationToken ct = default)
    {
        var r = await _db.Set<Roadmap>().AsNoTracking()
            .Include(x => x.Milestones).ThenInclude(m => m.Lessons)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (r is null) return null;                                        // 404
        if (r.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải roadmap của bạn");   // 403

        return Map(r, includeTheory: true);
    }

    /// <summary>
    /// Danh sách roadmap của chính user — keyset-paged (mẫu DB8/DB31), KHÔNG kèm cây milestone/lesson.
    ///
    /// Trước: <c>Include(Milestones).ThenInclude(Lessons)</c> + không phân trang ⇒ payload nhân theo
    /// cây (roadmap × milestone × lesson) và không có trần, cho một màn hình danh sách chỉ vẽ tiêu đề
    /// + ngày + trạng thái. Nay project thẳng <see cref="RoadmapSummaryResponse"/> trong SQL; ai cần
    /// cây đầy đủ thì gọi <c>GET /roadmaps/{id}</c> (giữ nguyên <see cref="RoadmapResponse"/>).
    /// Đã đối chiếu FE trước khi bỏ: trang danh sách không đọc <c>milestones</c> (chỉ trang chi tiết
    /// đọc, và nó gọi endpoint khác).
    /// </summary>
    public async Task<KeysetPage<RoadmapSummaryResponse>> ListAsync(
        Guid candidateId, string? cursor = null, int? limit = null, CancellationToken ct = default)
    {
        var take = KeysetPaging.ClampLimit(limit);
        var cur = KeysetCursor.Decode(cursor);

        var query = _db.Set<Roadmap>().AsNoTracking()
            .Where(x => x.CandidateId == candidateId);

        // Keyset (CreatedAt DESC, Id DESC) — Id tie-break để hai roadmap trùng created_at vẫn có thứ
        // tự tổng, không lặp/sót dòng khi lật trang.
        if (cur is not null)
            query = query.Where(x => x.CreatedAt < cur.CreatedAt
                || (x.CreatedAt == cur.CreatedAt && x.Id.CompareTo(cur.Id) < 0));

        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new RoadmapSummaryResponse(
                x.Id,
                x.JobCategory.ToString(),
                x.Level.ToString(),
                x.CvId,
                x.Status.ToString(),
                x.CreatedAt,
                x.CompletedAt))
            .ToListAsync(ct);

        var next = rows.Count == take
            ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;
        return new KeysetPage<RoadmapSummaryResponse>(rows, next);
    }

    // Đọc parsed_text của file thuộc về candidate. null → 404; khác chủ → 403; rỗng → 400 (mẫu CvAnalysisService).
    private async Task<string> ReadOwnedParsedTextAsync(
        Guid fileId, Guid candidateId, string label, CancellationToken ct)
    {
        var file = await _storage.GetMetadata(fileId, ct)
            ?? throw new KeyNotFoundException($"{label} không tồn tại");

        if (file.UserId != candidateId)
            throw new UnauthorizedAccessException($"Không phải file {label} của bạn");

        if (string.IsNullOrWhiteSpace(file.ParsedText))
            throw new InvalidOperationException($"{label} không đọc được nội dung");

        return file.ParsedText;
    }

    private static RoadmapResponse Map(Roadmap r, bool includeTheory) => new(
        r.Id,
        r.JobCategory.ToString(),
        r.Level.ToString(),
        r.CvId,
        r.Status.ToString(),
        r.Milestones.OrderBy(m => m.OrderNo).Select(m => new MilestoneResponse(
            m.Id,
            m.OrderNo,
            m.Title,
            m.FocusCriteria,
            m.Status.ToString(),
            m.Improvement is null
                ? null
                : m.Improvement.Select(kv => new MilestoneImprovementResponse(kv.Key, kv.Value)).ToList(),
            m.Lessons.OrderBy(l => l.OrderNo).Select(l => new LessonResponse(
                l.Id,
                l.OrderNo,
                l.Title,
                includeTheory ? l.TheoryContent : null,
                l.SessionId,
                l.Status.ToString(),
                // F15 — tài liệu đi CÙNG lý thuyết (sinh chung 1 lượt): view nào giấu theory thì
                // cũng giấu resources, tránh hiện "tài liệu" cho lesson chưa mở.
                includeTheory
                    ? l.Resources.Select(RoadmapLessonService.MapResource).ToList()
                    : [])).ToList()
        )).ToList(),
        r.CreatedAt,
        r.CompletedAt);
}
