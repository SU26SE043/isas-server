using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

// BC7 — B2C phân tích CV; BC7b — TÍNH PHÍ (BC-4/D22): reserve 1 credit ví User trước khi gọi AIService,
// consume khi lưu xong, release khi AIService lỗi (owner=User; B2B batch CV vẫn free — D19, không đụng).
public class CvAnalysisService : ICvAnalysisService
{
    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceCvAnalyzer _analyzer;
    private readonly ICreditReservationClient _reservationClient;   // BC7b
    private readonly int _cvAnalysisCredits;                        // BC7b (Billing:CvAnalysisCredits)
    private readonly ILogger<CvAnalysisService> _logger;

    // BC7b — 0 = miễn phí (kill-switch, bỏ qua reserve/consume/release); >0 = tính phí 1 credit/lần.
    private bool Billed => _cvAnalysisCredits > 0;

    public CvAnalysisService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceCvAnalyzer analyzer,
        ICreditReservationClient reservationClient,
        IConfiguration config,
        ILogger<CvAnalysisService> logger)
    {
        _db = db;
        _storage = storage;
        _analyzer = analyzer;
        _reservationClient = reservationClient;
        // Billing:CvAnalysisCredits (mặc định 1). Chỉ dùng indexer để không phụ thuộc Configuration.Binder.
        _cvAnalysisCredits = int.TryParse(config["Billing:CvAnalysisCredits"], out var credits) ? credits : 1;
        _logger = logger;
    }

    public async Task<CvAnalysisResponse> AnalyzeAsync(
        Guid candidateId, CvAnalysisRequest req, CancellationToken ct = default)
    {
        // CV bắt buộc — đọc file (kiểm chủ sở hữu + lấy parsed_text). 404/403/400 ném TRƯỚC reserve
        // → KHÔNG trừ/giữ credit oan (mẫu BC2 PracticeService: validate → reserve).
        var cvText = await ReadOwnedParsedTextAsync(req.CvId, candidateId, "CV", ct);

        // JD optional → gửi kèm để AI trả jdMatch.
        string? jdText = null;
        if (req.JdId is not null)
            jdText = await ReadOwnedParsedTextAsync(req.JdId.Value, candidateId, "JD", ct);

        // BC7b — operationId = Id row cv_analyses sắp tạo, dùng làm khoá reservation cho op không-session
        // này → consume/release idempotent theo đúng khoá (P4).
        var operationId = Guid.NewGuid();

        // BC7b — reserve 1 credit ví User TRƯỚC khi gọi AIService (mẫu BC2). Ví hết → Payment 402 →
        // InsufficientCreditException ném ở đây ⇒ KHÔNG gọi AI, KHÔNG có row cv_analyses (PAY-5).
        if (Billed)
        {
            await _reservationClient.ReserveAsync(
                ownerType: "User", ownerId: candidateId, sessionId: operationId, ct: ct);
            _logger.LogInformation(
                "Reserve 1 credit ví cá nhân cho phân tích CV {OperationId} (candidate {CandidateId})",
                operationId, candidateId);
        }

        CvAnalysis entity;
        try
        {
            // Gọi AIService (sync). Lỗi → AiServiceException (502).
            var ai = await _analyzer.AnalyzeAsync(req.JobCategory.ToString(), cvText, jdText, ct);

            entity = new CvAnalysis
            {
                Id = operationId,
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
        }
        catch (Exception ex)
        {
            // AIService/DB lỗi SAU reserve → hoàn chỗ giữ (không trừ credit) rồi rethrow (AI→502).
            // KHÔNG lưu row. Release best-effort (dùng CancellationToken.None để chạy cả khi ct huỷ).
            if (Billed)
            {
                _logger.LogWarning(ex,
                    "Phân tích CV {OperationId} lỗi sau reserve → release credit đã giữ", operationId);
                await ReleaseQuietlyAsync(operationId);
            }
            throw;
        }

        // Thành công → trừ phí thật (Reserved→Consumed, idempotent/absorbing PAY-11). Best-effort:
        // consume lỗi sau khi đã lưu row → credit treo (remaining đã giảm lúc reserve) → reconcile sau,
        // KHÔNG rollback row / KHÔNG bắt user chịu lỗi ledger.
        if (Billed)
            await ConsumeQuietlyAsync(operationId, ct);

        _logger.LogInformation(
            "CV analysis {Id} cho candidate {CandidateId} (cv={CvId}, jd={JdId})",
            entity.Id, candidateId, req.CvId, req.JdId);

        return Map(entity);
    }

    // BC7b — consume best-effort: lỗi Payment sau khi lưu row → KHÔNG fail phân tích (user đã có kết quả);
    // credit đã reserve (remaining giảm) → treo, reconcile sau. ⚠ ratify: cần active-polling đối soát.
    private async Task ConsumeQuietlyAsync(Guid operationId, CancellationToken ct)
    {
        try
        {
            await _reservationClient.ConsumeAsync(operationId, ct);
            _logger.LogInformation("Consume 1 credit cho phân tích CV {OperationId}", operationId);
        }
        catch (PaymentServiceException ex)
        {
            _logger.LogError(ex,
                "Consume credit lỗi cho phân tích CV {OperationId} → credit treo, cần reconcile", operationId);
        }
    }

    // BC7b — release best-effort khi op lỗi: lỗi Payment → credit treo, reconcile sau (không rethrow
    // đè lên lỗi gốc AI/DB đang được propagate).
    private async Task ReleaseQuietlyAsync(Guid operationId)
    {
        try
        {
            await _reservationClient.ReleaseAsync(operationId, CancellationToken.None);
        }
        catch (PaymentServiceException ex)
        {
            _logger.LogError(ex,
                "Release credit lỗi cho phân tích CV {OperationId} → credit treo, cần reconcile", operationId);
        }
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
