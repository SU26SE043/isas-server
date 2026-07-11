using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

// BC7 — B2C phân tích CV (miễn phí ở BC7 — reserve/consume credit là BC2/BC3, không wire ở đây).
public class CvAnalysisService : ICvAnalysisService
{
    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceCvAnalyzer _analyzer;
    private readonly ILogger<CvAnalysisService> _logger;

    public CvAnalysisService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceCvAnalyzer analyzer,
        ILogger<CvAnalysisService> logger)
    {
        _db = db;
        _storage = storage;
        _analyzer = analyzer;
        _logger = logger;
    }

    public async Task<CvAnalysisResponse> AnalyzeAsync(
        Guid candidateId, CvAnalysisRequest req, CancellationToken ct = default)
    {
        // CV bắt buộc — đọc file (kiểm chủ sở hữu + lấy parsed_text).
        var cvText = await ReadOwnedParsedTextAsync(req.CvId, candidateId, "CV", ct);

        // JD optional → gửi kèm để AI trả jdMatch.
        string? jdText = null;
        if (req.JdId is not null)
            jdText = await ReadOwnedParsedTextAsync(req.JdId.Value, candidateId, "JD", ct);

        // Gọi AIService (sync). Lỗi → AiServiceException (502) → KHÔNG tạo row.
        var ai = await _analyzer.AnalyzeAsync(req.JobCategory.ToString(), cvText, jdText, ct);

        var entity = new CvAnalysis
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            CvId = req.CvId,
            JdId = req.JdId,
            JobCategory = req.JobCategory,
            Summary = ai.Summary,
            Strengths = ai.Strengths,
            Weaknesses = ai.Weaknesses,
            Suggestions = ai.Suggestions,
            // jdMatch chỉ có ý nghĩa khi request có JD.
            JdMatch = req.JdId is not null ? ai.JdMatch : null,
            CreatedAt = DateTime.UtcNow
        };

        _db.Set<CvAnalysis>().Add(entity);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "CV analysis {Id} cho candidate {CandidateId} (cv={CvId}, jd={JdId})",
            entity.Id, candidateId, req.CvId, req.JdId);

        return Map(entity);
    }

    public async Task<CvAnalysisResponse?> GetAsync(
        Guid candidateId, Guid id, CancellationToken ct = default)
    {
        var row = await _db.Set<CvAnalysis>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (row is null) return null;                                  // 404
        if (row.CandidateId != candidateId)
            throw new UnauthorizedAccessException("Không phải phân tích của bạn");   // 403

        return Map(row);
    }

    public async Task<IReadOnlyList<CvAnalysisResponse>> ListAsync(
        Guid candidateId, CancellationToken ct = default)
    {
        var rows = await _db.Set<CvAnalysis>().AsNoTracking()
            .Where(x => x.CandidateId == candidateId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        return rows.Select(Map).ToList();
    }

    // Đọc parsed_text của file thuộc về candidate. null → 404; khác chủ → 403; rỗng → 400.
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

    private static CvAnalysisResponse Map(CvAnalysis e) => new(
        e.Id,
        e.CvId,
        e.JdId,
        e.JobCategory.ToString(),
        e.Summary,
        e.Strengths,
        e.Weaknesses,
        e.Suggestions,
        e.JdMatch is null
            ? null
            : new JdMatchResponse(e.JdMatch.Score, e.JdMatch.MatchedSkills, e.JdMatch.MissingSkills),
        e.CreatedAt);
}
