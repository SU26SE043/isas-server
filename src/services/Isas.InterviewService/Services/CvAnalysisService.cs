using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Isas.Shared.Validation;
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
    private readonly IEntitlementClient? _entitlements;
    private readonly bool _tieringEnabled;

    // BC7b — 0 = miễn phí (kill-switch, bỏ qua reserve/consume/release); >0 = tính phí 1 credit/lần.
    private bool Billed => _cvAnalysisCredits > 0;

    public CvAnalysisService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceCvAnalyzer analyzer,
        ICreditReservationClient reservationClient,
        IConfiguration config,
        ILogger<CvAnalysisService> logger,
        IEntitlementClient? entitlements = null)
    {
        _db = db;
        _storage = storage;
        _analyzer = analyzer;
        _reservationClient = reservationClient;
        // Billing:CvAnalysisCredits (mặc định 1). Chỉ dùng indexer để không phụ thuộc Configuration.Binder.
        _cvAnalysisCredits = int.TryParse(config["Billing:CvAnalysisCredits"], out var credits) ? credits : 1;
        _logger = logger;
        _entitlements = entitlements;
        _tieringEnabled = bool.TryParse(config["Tiering:Enabled"], out var enabled) && enabled;
    }

    public async Task<CvAnalysisResponse> AnalyzeAsync(
        Guid candidateId, CvAnalysisRequest req, CancellationToken ct = default)
    {
        if (_tieringEnabled && _entitlements is not null && !(await _entitlements.ResolveUserAsync(candidateId, ct)).CvAnalysisIncluded)
            throw new UnauthorizedAccessException("Gói hiện tại không bao gồm phân tích CV.");
        // BK6 — jobCategory BẮT BUỘC. Guard NGAY ĐẦU (trước cả đọc CV/reserve) → thiếu → 400
        // (controller map InvalidOperationException → BadRequest), KHÔNG giữ credit oan (PAY-5).
        // (HTTP thật cũng 400 sớm hơn nhờ [Required]; test gọi controller trực tiếp nên cần guard này.)
        if (req.JobCategory is null)
            throw new InvalidOperationException("jobCategory là bắt buộc.");
        var jobCategory = req.JobCategory.Value;

        // JD nhập tay: chuẩn hoá + cap độ dài NGAY ĐẦU, TRƯỚC cả đọc CV và reserve — guard rẻ nhất
        // (thuần in-memory) chạy trước → JD quá dài → 400 mà không tốn round-trip storage và KHÔNG giữ
        // credit oan (mẫu BK6/PAY-5).
        var jdTextInput = NormalizeText(req.JdText);

        // CV bắt buộc — đọc file (kiểm chủ sở hữu + lấy parsed_text). 404/403/400 ném TRƯỚC reserve
        // → KHÔNG trừ/giữ credit oan (mẫu BC2 PracticeService: validate → reserve).
        var cvText = await ReadOwnedParsedTextAsync(req.CvId, candidateId, "CV", ct);

        // JD optional → gửi kèm để AI trả jdMatch. 2 nguồn: text nhập thẳng (jdText) HOẶC file (jdId).
        // TEXT ƯU TIÊN FILE (quy ước C11 bên B2B/Campaign): gửi cả hai → text thắng, file KHÔNG đọc
        // (khỏi tốn round-trip + khỏi ownership-check cho file không dùng) và KHÔNG lưu jd_id.
        var jdIdToUse = jdTextInput is not null ? null : req.JdId;

        string? jdText = jdTextInput;
        if (jdTextInput is null && req.JdId is not null)
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
            var ai = await _analyzer.AnalyzeAsync(jobCategory.ToString(), cvText, jdText, ct);

            entity = new CvAnalysis
            {
                Id = operationId,
                CandidateId = candidateId,
                CvId = req.CvId,
                JdId = jdIdToUse,   // null khi JD đến từ text (C11: text ưu tiên file)
                JobCategory = jobCategory,
                Summary = ai.Summary,
                Strengths = ai.Strengths,
                Weaknesses = ai.Weaknesses,
                Suggestions = ai.Suggestions,
                // jdMatch chỉ có ý nghĩa khi request có JD — gate theo "CÓ NỘI DUNG JD" (text HOẶC file),
                // KHÔNG theo req.JdId: từ khi nhận jdText, gate cũ sẽ vứt jdMatch của mọi JD nhập tay.
                JdMatch = jdText is not null ? ai.JdMatch : null,
                RequirementMatches = ai.RequirementMatches?.ToList(),
                CvSections = ai.CvSections?.ToList(),
                Citations = ai.Citations?.ToList(),
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

        // jdSource để đọc log biết JD đến từ đâu (text nhập tay không có jd_id để lần vết).
        _logger.LogInformation(
            "CV analysis {Id} cho candidate {CandidateId} (cv={CvId}, jd={JdId}, jdSource={JdSource})",
            entity.Id, candidateId, req.CvId, jdIdToUse,
            jdTextInput is not null ? "text" : (jdIdToUse is not null ? "file" : "none"));

        return Map(entity);
    }

    // Chuẩn hoá text nhập tay: rỗng/toàn khoảng trắng = KHÔNG nhập (null), còn lại thì trim.
    // Giống CampaignService.NormalizeText (C11) → hành vi "gửi jdText rỗng" đồng nhất 2 dòng sản phẩm.
    // + cap độ dài (TextInputLimits.JdTextMaxChars — ngưỡng CHUNG với B2B/Campaign): JD nhập tay đi thẳng
    // vào prompt Gemini → vượt ngưỡng ném InvalidOperationException (controller map → 400) kèm giới hạn và
    // độ dài đang gửi. Đo SAU khi trim → khoảng trắng thừa không tính vào ngưỡng.
    private static string? NormalizeText(string? text)
        => TextInputLimits.NormalizeAndEnsureLimit(
            text, JdTextLabel, msg => new InvalidOperationException(msg));

    // Nhãn field trong thông báo lỗi 400 — khớp tên field client gửi lên.
    private const string JdTextLabel = "Mô tả công việc (jdText)";

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

    /// <summary>
    /// Danh sách phân tích CV của chính user — keyset-paged (mẫu DB8/DB31).
    ///
    /// Shape payload giữ NGUYÊN, cố ý: đã kiểm FE (`isas-frontend`) trước khi định cắt bớt và thấy
    /// trang này KHÔNG có màn chi tiết — danh sách CHÍNH LÀ chi tiết, mỗi dòng là một expansion panel
    /// render đủ <c>summary</c>/<c>strengths</c>/<c>weaknesses</c>/<c>suggestions</c>/<c>jdMatch</c>
    /// (endpoint `GET /cv-analysis/{id}` hiện không có consumer nào). Bỏ mấy mảng đó khỏi list sẽ
    /// KHÔNG chỉ là thiếu chữ: chúng là `string[]` non-optional được `@for` duyệt thẳng, `undefined`
    /// vào đó là văng runtime, trắng cả mục lịch sử.
    ///
    /// ⇒ ở đây chỉ chặn số dòng (trần 500/trang) chứ không đụng shape. Muốn list gọn thật thì phải
    /// làm trang chi tiết trước (BE + FE cùng nhịp), không phải việc của vòng này.
    /// </summary>
    public async Task<KeysetPage<CvAnalysisResponse>> ListAsync(
        Guid candidateId, string? cursor = null, int? limit = null, CancellationToken ct = default)
    {
        var take = KeysetPaging.ClampLimit(limit);
        var cur = KeysetCursor.Decode(cursor);

        var query = _db.Set<CvAnalysis>().AsNoTracking()
            .Where(x => x.CandidateId == candidateId);

        // Keyset (CreatedAt DESC, Id DESC) — Id tie-break: hai phân tích trùng created_at (bấm liên
        // tiếp) vẫn có thứ tự tổng, không lặp/sót dòng khi lật trang.
        if (cur is not null)
            query = query.Where(x => x.CreatedAt < cur.CreatedAt
                || (x.CreatedAt == cur.CreatedAt && x.Id.CompareTo(cur.Id) < 0));

        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .ToListAsync(ct);

        var items = rows.Select(Map).ToList();
        var next = items.Count == take
            ? new KeysetCursor(items[^1].CreatedAt, items[^1].Id).Encode()
            : null;
        return new KeysetPage<CvAnalysisResponse>(items, next);
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

    private static CvAnalysisResponse Map(CvAnalysis e)
    {
        var matches = e.RequirementMatches;
        var mustHave = matches?.Where(x => x.Priority == "MustHave").ToList();
        var niceToHave = matches?.Where(x => x.Priority == "NiceToHave").ToList();

        return new CvAnalysisResponse(
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
            e.CreatedAt,
            mustHave,
            niceToHave,
            BuildRequirementSummary(matches),
            e.CvSections,
            e.Citations);
    }

    private static RequirementSummary? BuildRequirementSummary(
        IReadOnlyList<CvRequirementMatch>? matches)
    {
        if (matches is null) return null;

        static RequirementSummaryBucket Bucket(IEnumerable<CvRequirementMatch> items)
        {
            var list = items.ToList();
            return new RequirementSummaryBucket(
                list.Count,
                list.Count(x => x.Level == "Strong"),
                list.Count(x => x.Level == "Partial"),
                list.Count(x => x.Level == "Weak"));
        }

        return new RequirementSummary(
            Bucket(matches.Where(x => x.Priority == "MustHave")),
            Bucket(matches.Where(x => x.Priority == "NiceToHave")));
    }
}
