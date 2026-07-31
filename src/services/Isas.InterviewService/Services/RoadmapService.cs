using System.Text;
using System.Text.Json;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// BC12 (D20) — roadmap ôn tập cá nhân hoá B2C. Gom điểm yếu (session_criterion_scores, BC9) + CV
// → AIService /generate-roadmap (sync) → LƯU 3 bảng (AI KHÔNG ghi DB). Tạo roadmap KHÔNG trừ credit
// (D7/D15 — chỉ session luyện BC14 mới reserve). AI lỗi → 502, KHÔNG lưu gì (gọi AI trước khi Add).
// BC17 — candidate CHỌN nguồn: SessionIds (baseline), CvAnalysisId + PriorRoadmapId + Focus (bối cảnh
// prompt, KHÔNG vào baseline). Rỗng SessionIds → roadmap CHUẨN theo level (thôi tự gom mọi buổi Scored).
public class RoadmapService : IRoadmapService
{
    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceRoadmapGenerator _generator;
    private readonly IKnowledgeService? _knowledge;   // RAG grounding — precompute (null = tắt)
    private readonly GroundingOptions _grounding;     // RAG grounding — Enabled/TopK/threshold
    private readonly ILogger<RoadmapService> _logger;

    public RoadmapService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceRoadmapGenerator generator,
        ILogger<RoadmapService> logger,
        // RAG grounding — optional (default null/tắt): test cũ dựng 4 tham số vẫn compile + precompute tắt.
        IKnowledgeService? knowledge = null,
        IOptions<GroundingOptions>? groundingOptions = null)
    {
        _db = db;
        _storage = storage;
        _generator = generator;
        _knowledge = knowledge;
        _grounding = groundingOptions?.Value ?? new GroundingOptions();
        _logger = logger;
    }

    public async Task<RoadmapResponse> CreateAsync(
        Guid candidateId, CreateRoadmapRequest req, CancellationToken ct = default)
    {
        // CV optional — đọc parsed_text (kiểm chủ sở hữu). null → 404; khác chủ → 403; rỗng → 400.
        string? cvText = null;
        if (req.CvId is not null)
            cvText = await ReadOwnedParsedTextAsync(req.CvId.Value, candidateId, "CV", ct);

        // BC17 — baseline lấy từ CÁC BUỔI CANDIDATE CHỌN (thôi tự gom MỌI buổi Scored). SessionIds
        // rỗng/null → roadmap CHUẨN theo level (baseline/weakness/sources = null, KHÔNG query buổi nào).
        Dictionary<string, decimal>? baseline = null;
        List<RoadmapWeakness>? weaknesses = null;
        List<Guid>? sourceSessionIds = null;

        if (req.SessionIds is { Count: > 0 })
        {
            var requestedIds = req.SessionIds.Distinct().ToList();

            // CHỈ những buổi được chọn, owner-scoped + B2C + đã Scored (BC-3). Không phủ đủ MỌI id yêu
            // cầu → 404 batch (KHÔNG lộ id nào thiếu / không thuộc mình / chưa chấm).
            var chosen = await _db.PracticeSessions.AsNoTracking()
                .Include(s => s.CriterionScores)
                .Where(s => requestedIds.Contains(s.Id)
                            && s.CandidateId == candidateId
                            && s.CampaignId == null
                            && s.Status == SessionStatus.Scored)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(ct);

            if (chosen.Count != requestedIds.Count)
                throw new KeyNotFoundException(
                    "Một số buổi luyện không tồn tại, không thuộc về bạn, hoặc chưa được chấm.");

            // Newest-first: tiêu chí xuất hiện lần đầu (buổi mới nhất) thắng → baseline = % hiện tại.
            var withScores = chosen.Where(s => s.CriterionScores.Count > 0).ToList();
            if (withScores.Count > 0)
            {
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

                weaknesses = weak.Count > 0 ? weak : null;
            }

            // sourceSessionIds = ĐÚNG các buổi được chọn (đều đã Scored/owned nhờ guard phủ ở trên).
            sourceSessionIds = chosen.Select(s => s.Id).ToList();
        }

        // BC17 — phân tích CV đã có (BC7) làm NGỮ CẢNH prompt. CHỈ ĐỌC row đã lưu — KHÔNG gọi lại
        // /analyze-cv, KHÔNG reserve/consume credit (D22, tạo roadmap free). Thiếu → 404; khác chủ → 403.
        string? cvAnalysisSummary = null;
        if (req.CvAnalysisId is not null)
        {
            var ca = await _db.Set<CvAnalysis>().AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == req.CvAnalysisId.Value, ct)
                ?? throw new KeyNotFoundException("Phân tích CV không tồn tại.");
            if (ca.CandidateId != candidateId)
                throw new UnauthorizedAccessException("Không phải phân tích CV của bạn");
            cvAnalysisSummary = BuildCvAnalysisSummary(ca);
        }

        // BC17 — final_report của roadmap đã hoàn thành (BC15) làm NGỮ CẢNH. Thiếu → 404; khác chủ → 403;
        // chưa có báo cáo (chưa hoàn thành) → 400.
        string? priorRoadmapSummary = null;
        if (req.PriorRoadmapId is not null)
        {
            var prior = await _db.Set<Roadmap>().AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == req.PriorRoadmapId.Value, ct)
                ?? throw new KeyNotFoundException("Roadmap được chọn không tồn tại.");
            if (prior.CandidateId != candidateId)
                throw new UnauthorizedAccessException("Không phải roadmap của bạn");
            if (string.IsNullOrWhiteSpace(prior.FinalReport))
                throw new InvalidOperationException("Roadmap được chọn chưa có báo cáo (chưa hoàn thành).");

            priorRoadmapSummary = BuildPriorRoadmapSummary(prior.FinalReport, prior.Id);
        }

        // BC17 — mô tả tự do: trim (khoảng-trắng-thuần = không nhập) + cap độ dài → vượt 400. Chống
        // prompt-injection (bọc như dữ liệu) là việc phía AIService (worker Python), không phải ở đây.
        string? focus = null;
        if (!string.IsNullOrWhiteSpace(req.Focus))
        {
            focus = req.Focus.Trim();
            if (focus.Length > FocusMaxChars)
                throw new InvalidOperationException($"Mô tả focus vượt quá {FocusMaxChars} ký tự.");
        }

        // Gọi AIService sinh cấu trúc (sync). Lỗi → AiServiceException (502) → KHÔNG lưu gì.
        var ai = await _generator.GenerateAsync(
            req.JobCategory.ToString(), req.Level.ToString(), weaknesses, cvText,
            focus, cvAnalysisSummary, priorRoadmapSummary, ct);

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

        // RAG grounding (Cách 2 — precompute): batch-embed query từng lesson (tên bài + focus milestone +
        // jobCategory) trong 1 lần /embed → Qdrant search → LƯU snapshot vào lesson.GroundingRefs. Lúc MỞ
        // lesson (OpenLessonAsync) feed thẳng snapshot này, KHÔNG retrieve realtime (không thêm độ trễ lazy).
        // best-effort — RetrieveBatchAsync tự degrade rỗng khi lỗi; wrap để có sự cố lạ vẫn KHÔNG chặn tạo roadmap.
        if (_grounding.Enabled && _knowledge is not null)
            await PrecomputeLessonGroundingAsync(roadmap, req.JobCategory.ToString(), ct);

        _db.Set<Roadmap>().Add(roadmap);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "BC12: roadmap {Id} candidate {CandidateId} ({Cat}/{Level}) milestones={M} sources={S}",
            roadmap.Id, candidateId, req.JobCategory, req.Level,
            roadmap.Milestones.Count, sourceSessionIds?.Count ?? 0);

        return Map(roadmap, includeTheory: true);
    }

    // RAG grounding (Cách 2) — precompute snapshot cho MỌI lesson trong roadmap. 1 lần /embed cho tất cả
    // query → search từng lesson → set GroundingRefs (LIST rỗng nếu miss — grounding ĐÃ chạy nên KHÔNG null,
    // phân biệt với roadmap cũ chưa precompute = null). Query = tên bài + focus milestone + jobCategory.
    private async Task PrecomputeLessonGroundingAsync(Roadmap roadmap, string jobCategory, CancellationToken ct)
    {
        // Duyệt lesson theo đúng thứ tự để map kết quả batch về đúng lesson.
        var flat = roadmap.Milestones
            .SelectMany(m => m.Lessons.Select(l => (Lesson: l, Milestone: m)))
            .ToList();
        if (flat.Count == 0) return;

        var queries = flat.Select(x =>
        {
            var parts = new List<string> { x.Lesson.Title, jobCategory };
            if (x.Milestone.FocusCriteria is { Count: > 0 } focus)
                parts.Insert(1, string.Join(", ", focus));
            return string.Join("\n", parts);
        }).ToList();

        IReadOnlyList<IReadOnlyList<GroundingChunk>> batches;
        try
        {
            batches = await _knowledge!.RetrieveBatchAsync(jobCategory, queries, ct);
        }
        catch (Exception ex)
        {
            // RetrieveBatchAsync vốn tự degrade; wrap phòng lỗi lạ → precompute rỗng, KHÔNG chặn tạo roadmap.
            _logger.LogWarning(ex, "RAG grounding: precompute roadmap lỗi → grounding_refs=[] (ungrounded)");
            foreach (var (lesson, _) in flat) lesson.GroundingRefs = new List<GroundingChunk>();
            return;
        }

        for (int i = 0; i < flat.Count; i++)
            // grounding ĐÃ chạy → LUÔN set list (rỗng = ungrounded), KHÔNG để null.
            flat[i].Lesson.GroundingRefs = (batches.Count > i ? batches[i] : Array.Empty<GroundingChunk>()).ToList();

        var grounded = flat.Count(x => x.Lesson.GroundingRefs is { Count: > 0 });
        _logger.LogInformation(
            "RAG grounding: precompute roadmap {Id} — {Grounded}/{Total} lesson có nguồn",
            roadmap.Id, grounded, flat.Count);
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

    // BC17 — trần độ dài. focus: cap input tự do (rẻ, chống prompt phình). summary: cắt bối cảnh gửi AI
    // (giữ HttpClient timeout + chi phí token trong tầm — nhồi nhiều report/CV dễ vượt).
    private const int FocusMaxChars = 2000;
    private const int SummaryMaxChars = 4000;

    // BC17 — deserialize final_report khớp cách RoadmapReportService serialize (Web defaults).
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // BC17 — dựng bối cảnh text từ 1 phân tích CV (BC7) đã lưu: summary + strengths/weaknesses/suggestions
    // + mức khớp JD (nếu có). Cắt ≤ SummaryMaxChars.
    private static string BuildCvAnalysisSummary(CvAnalysis ca)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ca.Summary))
            sb.Append("Tóm tắt CV: ").AppendLine(ca.Summary.Trim());
        if (ca.Strengths.Count > 0)
            sb.Append("Điểm mạnh: ").AppendLine(string.Join("; ", ca.Strengths));
        if (ca.Weaknesses.Count > 0)
            sb.Append("Điểm yếu: ").AppendLine(string.Join("; ", ca.Weaknesses));
        if (ca.Suggestions.Count > 0)
            sb.Append("Gợi ý: ").AppendLine(string.Join("; ", ca.Suggestions));
        if (ca.JdMatch is not null)
            sb.Append("Mức khớp JD: ").Append(ca.JdMatch.Score).AppendLine("%");
        return Truncate(sb.ToString().Trim(), SummaryMaxChars);
    }

    // BC17 — dựng bối cảnh text từ final_report roadmap trước (BC15): overallComment + strengths/weaknesses
    // /improvements. Cắt ≤ SummaryMaxChars. final_report hỏng (defensive, đáng lẽ không xảy ra) → null.
    private string? BuildPriorRoadmapSummary(string finalReportJson, Guid roadmapId)
    {
        RoadmapReportResponse? report;
        try
        {
            report = JsonSerializer.Deserialize<RoadmapReportResponse>(finalReportJson, WebJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "BC17: final_report roadmap {RoadmapId} hỏng → bỏ qua bối cảnh", roadmapId);
            return null;
        }
        if (report is null) return null;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(report.OverallComment))
            sb.Append("Nhận xét roadmap trước: ").AppendLine(report.OverallComment!.Trim());
        if (report.Strengths.Count > 0)
            sb.Append("Điểm mạnh: ").AppendLine(string.Join("; ", report.Strengths));
        if (report.Weaknesses.Count > 0)
            sb.Append("Điểm yếu: ").AppendLine(string.Join("; ", report.Weaknesses));
        if (report.Improvements.Count > 0)
            sb.Append("Đã cải thiện / cần luyện tiếp: ").AppendLine(string.Join("; ", report.Improvements));

        var text = sb.ToString().Trim();
        return text.Length == 0 ? null : Truncate(text, SummaryMaxChars);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

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
                    : [],
                // RAG grounding — CHỈ hiện citation khi lý thuyết ĐÃ sinh (grounding_refs lúc đó = tập AI thật
                // sự cite, narrow ở OpenLessonAsync). Chưa mở → null (chưa claim nguồn nào).
                includeTheory && l.TheoryContent != null
                    ? GroundingMapper.ToCitations(l.GroundingRefs)
                    : null)).ToList()
        )).ToList(),
        r.CreatedAt,
        r.CompletedAt);
}
