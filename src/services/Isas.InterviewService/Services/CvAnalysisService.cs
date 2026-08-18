using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Isas.Shared.Validation;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// BC7 — B2C phân tích CV; BC7b — TÍNH PHÍ (BC-4/D22): reserve 1 credit ví User trước khi gọi AIService,
// consume khi lưu xong, release khi AIService lỗi (owner=User; B2B batch CV vẫn free — D19, không đụng).
public class CvAnalysisService : ICvAnalysisService
{
    private readonly InterviewDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAiServiceCvAnalyzer _analyzer;
    private readonly IKnowledgeService? _knowledge;
    private readonly IConfiguration _config;
    private readonly ICreditReservationClient _reservationClient;   // BC7b
    private readonly int _cvAnalysisCredits;                        // BC7b (Billing:CvAnalysisCredits)
    private readonly ILogger<CvAnalysisService> _logger;
    private readonly IEntitlementClient? _entitlements;
    private readonly bool _tieringEnabled;
    private readonly GroundingOptions _grounding;

    // BC7b — 0 = miễn phí (kill-switch, bỏ qua reserve/consume/release); >0 = tính phí 1 credit/lần.
    private bool Billed => _cvAnalysisCredits > 0;

    public CvAnalysisService(
        InterviewDbContext db,
        IStorageService storage,
        IAiServiceCvAnalyzer analyzer,
        ICreditReservationClient reservationClient,
        IConfiguration config,
        ILogger<CvAnalysisService> logger,
        IEntitlementClient? entitlements = null,
        IKnowledgeService? knowledge = null,
        IOptions<GroundingOptions>? groundingOptions = null)
    {
        _db = db;
        _storage = storage;
        _analyzer = analyzer;
        _knowledge = knowledge;
        _config = config;
        _reservationClient = reservationClient;
        // Billing:CvAnalysisCredits (mặc định 1). Chỉ dùng indexer để không phụ thuộc Configuration.Binder.
        _cvAnalysisCredits = int.TryParse(config["Billing:CvAnalysisCredits"], out var credits) ? credits : 1;
        _logger = logger;
        _entitlements = entitlements;
        _tieringEnabled = bool.TryParse(config["Tiering:Enabled"], out var enabled) && enabled;
        _grounding = groundingOptions?.Value ?? new GroundingOptions();
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

        var requirementMode = req.MustHave is not null || req.NiceToHave is not null;
        var normalizedRequirements = requirementMode
            ? NormalizeRequirements(req.MustHave, req.NiceToHave)
            : [];
        if (requirementMode)
            ValidateRequirementLimits(normalizedRequirements);

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

        IReadOnlyList<GroundingChunk> grounding = [];
        IReadOnlyList<CvRequirementInput> mustHave = [];
        IReadOnlyList<CvRequirementInput> niceToHave = [];
        if (requirementMode)
        {
            mustHave = normalizedRequirements
                .Where(x => x.Priority == "MustHave")
                .Select(x => new CvRequirementInput(x.RequirementId, x.Text))
                .ToList();
            niceToHave = normalizedRequirements
                .Where(x => x.Priority == "NiceToHave")
                .Select(x => new CvRequirementInput(x.RequirementId, x.Text))
                .ToList();

            if (_grounding.Enabled && _knowledge is not null)
            {
                var batches = await _knowledge.RetrieveBatchAsync(
                    jobCategory.ToString(), normalizedRequirements.Select(x => x.Text).ToList(), ct);
                grounding = batches.SelectMany(x => x)
                    .GroupBy(x => x.ChunkId, StringComparer.Ordinal)
                    .Select(x => x.First())
                    .ToList();
            }
        }

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
            var ai = requirementMode
                ? await _analyzer.AnalyzeAsync(
                    jobCategory.ToString(), cvText, jdText, ct, mustHave, niceToHave, grounding)
                : await _analyzer.AnalyzeAsync(jobCategory.ToString(), cvText, jdText, ct);

            var matches = requirementMode
                ? NormalizeRequirementMatches(ai.RequirementMatches, normalizedRequirements, cvText, ai.CvSections)
                : null;

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
                JdMatch = requirementMode ? null : (jdText is not null ? ai.JdMatch : null),
                RequirementMatches = matches,
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

    private const int DefaultMaxRequirementItems = 20;
    private const int DefaultMaxRequirementTextChars = 500;

    private sealed record NormalizedRequirement(string RequirementId, string Priority, string Text);

    private static List<NormalizedRequirement> NormalizeRequirements(
        IReadOnlyList<CvRequirementInput>? mustHave,
        IReadOnlyList<CvRequirementInput>? niceToHave)
    {
        var result = new List<NormalizedRequirement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<CvRequirementInput>? source, string priority)
        {
            foreach (var item in source ?? [])
            {
                var text = NormalizeRequirementText(item.Text);
                if (text is null) continue;
                var key = string.Join(' ', text.Normalize(NormalizationForm.FormKC)
                    .ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (!seen.Add(key)) continue;
                result.Add(new NormalizedRequirement(Guid.NewGuid().ToString("N"), priority, text));
            }
        }

        Add(mustHave, "MustHave");
        Add(niceToHave, "NiceToHave");
        return result;
    }

    private static string? NormalizeRequirementText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Trim();
    }

    private void ValidateRequirementLimits(IReadOnlyList<NormalizedRequirement> requirements)
    {
        var maxItems = int.TryParse(_config["JdRequirements:MaxItems"], out var configuredItems)
            ? configuredItems : DefaultMaxRequirementItems;
        var maxChars = int.TryParse(_config["JdRequirements:MaxTextChars"], out var configuredChars)
            ? configuredChars : DefaultMaxRequirementTextChars;
        if (requirements.Count > maxItems)
            throw new InvalidOperationException(
                $"Số requirement tối đa là {maxItems} (đang gửi {requirements.Count}).");
        var tooLong = requirements.FirstOrDefault(x => x.Text.Length > maxChars);
        if (tooLong is not null)
            throw new InvalidOperationException(
                $"Mỗi requirement tối đa {maxChars} ký tự (requirement đang dài {tooLong.Text.Length} ký tự).");
    }

    private static List<CvRequirementMatch> NormalizeRequirementMatches(
        IReadOnlyList<CvRequirementMatch>? rawMatches,
        IReadOnlyList<NormalizedRequirement> requirements,
        string cvText,
        IReadOnlyList<CvSectionAnchor>? sections)
    {
        if (rawMatches is null)
            throw new AiServiceException("AIService không trả requirementMatches.");

        var allowed = requirements.ToDictionary(x => x.RequirementId, StringComparer.Ordinal);
        var normalized = new Dictionary<string, CvRequirementMatch>(StringComparer.Ordinal);
        foreach (var raw in rawMatches)
        {
            if (!allowed.TryGetValue(raw.RequirementId, out var requirement)
                || normalized.ContainsKey(raw.RequirementId))
                continue;

            var level = raw.Level is "Strong" or "Partial" or "Weak" ? raw.Level : "Weak";
            var evidence = raw.Evidence?.Trim() ?? string.Empty;
            var evidenceMatch = FindVerbatim(cvText, evidence);
            int? page = null;
            string? sectionTitle = null;
            if (string.IsNullOrWhiteSpace(evidence)
                || evidence == NoEvidence
                || evidenceMatch is null)
            {
                level = "Weak";
                evidence = NoEvidence;
            }
            else
            {
                // Offset thuộc normalizedCv, nên page cũng phải đếm trên chính chuỗi đó.
                page = 1 + evidenceMatch.Value.Text[..evidenceMatch.Value.Offset].Count(c => c == '\n');
                sectionTitle = FindSectionTitle(cvText, sections, evidenceMatch.Value.Offset);
            }

            normalized[raw.RequirementId] = new CvRequirementMatch(
                requirement.RequirementId,
                requirement.Priority,
                requirement.Text,
                level,
                evidence,
                page,
                sectionTitle);
        }

        var missing = requirements
            .Where(x => !normalized.ContainsKey(x.RequirementId))
            .Select(x => x.RequirementId)
            .ToList();
        if (missing.Count > 0)
            throw new AiServiceException(
                $"AIService thiếu requirementMatches: {string.Join(", ", missing)}");

        return requirements.Select(x => normalized[x.RequirementId]).ToList();
    }

    private const string NoEvidence = "Không thấy bằng chứng";

    private static (string Text, int Offset)? FindVerbatim(string cvText, string evidence)
    {
        var normalizedCv = NormalizeVerbatim(cvText);
        var normalizedEvidence = NormalizeVerbatim(evidence).Trim();
        if (normalizedEvidence.Length == 0) return null;
        var compactEvidence = Regex.Replace(normalizedEvidence, @"[\s-]+", string.Empty);
        if (compactEvidence.Length == 0) return null;

        var pattern = Regex.Escape(normalizedEvidence)
            .Replace(@"\ ", "__SPACE__")
            .Replace("-", "__HYPHEN__")
            .Replace("__SPACE__", @"\s+")
            .Replace("__HYPHEN__", @"(?:-|\s)?");
        var match = Regex.Match(normalizedCv, pattern, RegexOptions.IgnoreCase);
        if (match.Success) return (normalizedCv, match.Index);

        // PDF có thể tách một từ thành `micro-\nservices`. Fallback này đồng bộ với Python:
        // bỏ separator để so khớp, nhưng map offset ngược về normalizedCv.
        var compact = new StringBuilder();
        var offsets = new List<int>();
        for (var i = 0; i < normalizedCv.Length; i++)
        {
            if (char.IsWhiteSpace(normalizedCv[i]) || normalizedCv[i] == '-') continue;
            compact.Append(normalizedCv[i]);
            offsets.Add(i);
        }

        var compactIndex = compact.ToString().IndexOf(compactEvidence, StringComparison.OrdinalIgnoreCase);
        return compactIndex >= 0 ? (normalizedCv, offsets[compactIndex]) : null;
    }

    private static string? FindSectionTitle(
        string cvText,
        IReadOnlyList<CvSectionAnchor>? sections,
        int evidenceOffset)
    {
        if (sections is null || sections.Count == 0) return null;

        return sections
            .Select(x => (x.Title, Offset: FindVerbatim(cvText, x.StartsWith)?.Offset))
            .Where(x => x.Offset is not null && x.Offset.Value <= evidenceOffset)
            .OrderByDescending(x => x.Offset)
            .Select(x => x.Title)
            .FirstOrDefault();
    }

    private static string NormalizeVerbatim(string value)
        => (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Replace('\u00A0', ' ')
            .Replace('\u2009', ' ')
            .Replace('\u202F', ' ')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('‘', '\'')
            .Replace('’', '\'')
            .Replace('–', '-')
            .Replace('—', '-')
            .Replace('‐', '-')
            .ToLowerInvariant();

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
    public async Task<KeysetPage<CvAnalysisListResponse>> ListAsync(
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

        var items = rows.Select(MapList).ToList();
        var next = items.Count == take
            ? new KeysetCursor(items[^1].CreatedAt, items[^1].Id).Encode()
            : null;
        return new KeysetPage<CvAnalysisListResponse>(items, next);
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

    private static CvAnalysisListResponse MapList(CvAnalysis e)
    {
        var matches = e.RequirementMatches;
        static CvRequirementListItem Slim(CvRequirementMatch x)
            => new(x.RequirementId, x.Priority, x.Text, x.Level);

        return new CvAnalysisListResponse(
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
            matches?.Where(x => x.Priority == "MustHave").Select(Slim).ToList(),
            matches?.Where(x => x.Priority == "NiceToHave").Select(Slim).ToList(),
            BuildRequirementSummary(matches));
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
