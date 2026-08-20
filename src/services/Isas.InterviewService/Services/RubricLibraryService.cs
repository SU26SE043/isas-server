using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.Shared.Rubric;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Services;

/// <summary>
/// BC16 — Rubric CÁ NHÂN B2C: candidate tự quản bộ tiêu chí luyện tập theo <see cref="JobCategory"/>
/// (không admin). Chưa khai → dùng seed mặc định (BC11). Scoring resolve ưu-tiên-riêng-else-mặc-định
/// qua <see cref="B2CRubricScope"/>. Điểm tổng vẫn TB cộng (INT-10) — weight chỉ để hiển thị.
///
/// <para><b>Q9</b> — đơn vị của rubric là <b>(candidate, nghề, NGÔN NGỮ)</b>, không phải (candidate, nghề).
/// Trước Q9 service này mù ngôn ngữ: mọi truy vấn gộp cả hai ngôn ngữ và <c>ReplaceAsync</c> không set
/// <c>Language</c> ⇒ <c>GET</c> trả 14 tiêu chí Σweight=2.0, rồi <c>PUT</c> chính payload đó → 400
/// "Tổng weight phải xấp xỉ 1" — API tự từ chối chính mình, ứng viên không có đường khai rubric riêng.</para>
///
/// <para><b>Additive</b>: <c>language</c> tuỳ chọn, mặc định <c>"vi"</c> ⇒ client không gửi param nhận
/// đúng rubric tiếng Việt như trước ⇒ FE không phải sửa dòng nào.</para>
///
/// <para><b>SC2</b> — <see cref="ReplaceAsync"/> KẾ THỪA <see cref="ScoringScope"/> từ tiêu chí bộ
/// chuẩn cùng <c>(nghề, ngôn ngữ, tên)</c>. Trước SC2 nó không set cột này ⇒ mọi tiêu chí riêng nhận
/// default <c>Always</c> ⇒ <c>LoadTargetableCriteriaAsync</c> trả rỗng ⇒ câu hỏi của buổi luyện KHÔNG
/// được gắn nhãn tiêu chí ⇒ mọi câu bị chấm trên TOÀN BỘ rubric. Đo trên prod: 400/593 câu (67%)
/// trắng nhãn, 37/96 buổi (39%) hỏng trọn vẹn, chỉ đúng hai (nghề, ngôn ngữ) có rubric riêng.
/// Backfill 16 dòng đang tồn tại: <c>scripts/backfill-rubric-scoring-scope.sql</c> (chạy TAY).</para>
/// </summary>
public interface IRubricLibraryService
{
    /// <summary>Rubric HIỆU LỰC cho 1 (nghề, ngôn ngữ): rubric riêng nếu có active, else seed mặc định (template).</summary>
    Task<RubricResponse> GetEffectiveAsync(
        Guid candidateId, JobCategory jobCategory, string? language = null, CancellationToken ct = default);

    /// <summary>Thay TOÀN BỘ rubric riêng của 1 (nghề, ngôn ngữ) (replace-all, soft-versioned, FK-safe).</summary>
    Task<RubricResponse> ReplaceAsync(
        Guid candidateId, JobCategory jobCategory, UpsertRubricRequest request,
        string? language = null, CancellationToken ct = default);

    /// <summary>Xoá rubric riêng của 1 (nghề, ngôn ngữ) → quay về seed mặc định (soft-deactivate, idempotent).</summary>
    Task ResetAsync(
        Guid candidateId, JobCategory jobCategory, string? language = null, CancellationToken ct = default);
}

public class RubricLibraryService : IRubricLibraryService
{
    private readonly InterviewDbContext _db;
    private readonly bool _bilingualEnabled;
    private readonly ILogger<RubricLibraryService> _logger;

    // `config` + `logger` optional (mẫu RoadmapService): DI tự resolve IConfiguration/ILogger, còn test
    // dựng thẳng `new RubricLibraryService(db)` vẫn compile và chạy ở chế độ đơn ngữ như trước.
    public RubricLibraryService(
        InterviewDbContext db, IConfiguration? config = null, ILogger<RubricLibraryService>? logger = null)
    {
        _db = db;
        _bilingualEnabled = bool.TryParse(config?["Interview:Bilingual:Enabled"], out var bilingual) && bilingual;
        _logger = logger ?? NullLogger<RubricLibraryService>.Instance;
    }

    public async Task<RubricResponse> GetEffectiveAsync(
        Guid candidateId, JobCategory jobCategory, string? language = null, CancellationToken ct = default)
    {
        var lang = ValidateLanguage(language);

        var owner = await B2CRubricScope.ResolveOwnerAsync(_db, candidateId, jobCategory, lang, ct);
        if (owner is Guid oid)
        {
            var custom = await _db.RubricCriteria.AsNoTracking().Include(c => c.Levels)
                .Where(c => c.CampaignId == null && c.CandidateId == oid && c.JobCategory == jobCategory
                        && c.Language == lang && c.IsActive)
                .OrderByDescending(c => c.Weight).ThenBy(c => c.Name)
                .ToListAsync(ct);
            return new RubricResponse(jobCategory, IsCustom: true, custom.Select(Map).ToList());
        }

        // Chưa có rubric riêng → seed mặc định làm template (FE clone rồi sửa).
        // ⚠ Vế `Language` ở ĐÂY mới là vế phục vụ đa số người dùng: toàn DB prod chỉ có 8 row rubric
        // riêng, còn seed là 7 tiêu chí × 3 nghề × 2 ngôn ngữ. Vá mỗi nhánh custom bên trên thì triệu
        // chứng Q9 (14 tiêu chí Σ=2.0) vẫn còn nguyên với gần như mọi ứng viên.
        // `.Include(Levels)` ở ĐÂY là vế đóng nghịch lý: mốc do admin soạn đi theo template sang form
        // sửa, nên ứng viên bấm "tuỳ chỉnh" là đã có sẵn thang để chỉnh, không phải trang trắng.
        var seed = await _db.RubricCriteria.AsNoTracking().Include(c => c.Levels)
            .Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == jobCategory
                        && c.Language == lang && c.IsActive)
            .OrderByDescending(c => c.Weight).ThenBy(c => c.Name)
            .ToListAsync(ct);
        return new RubricResponse(jobCategory, IsCustom: false, seed.Select(Map).ToList());
    }

    public async Task<RubricResponse> ReplaceAsync(
        Guid candidateId, JobCategory jobCategory, UpsertRubricRequest request,
        string? language = null, CancellationToken ct = default)
    {
        // Ngôn ngữ trước trọng số: báo đúng vấn đề đầu tiên thay vì bắt sửa Σweight rồi mới lộ ra
        // là ngôn ngữ sai. Cả hai đều InvalidOperationException → 400 nên không đổi mã lỗi.
        var lang = ValidateLanguage(language);
        var normalized = NormalizeAndValidate(request?.Criteria);   // throw InvalidOperationException → 400

        // FK-safe: KHÔNG hard-delete (answer_scores cũ có thể trỏ vào — FK Restrict). Soft-deactivate
        // bản active hiện tại + thêm bản mới active (Version++). Scoring luôn lọc IsActive → chọn bản mới.
        // ⚠ Vế `Language` là BẮT BUỘC: thiếu nó, lưu rubric tiếng Anh sẽ deactivate luôn rubric tiếng
        // Việt của chính ứng viên đó — mất bộ tiêu chí đang dùng mà không có lỗi nào báo.
        var current = await _db.RubricCriteria
            .Where(c => c.CampaignId == null && c.CandidateId == candidateId
                        && c.JobCategory == jobCategory && c.Language == lang && c.IsActive)
            .ToListAsync(ct);
        foreach (var c in current) c.IsActive = false;

        // Đánh số phiên bản RIÊNG theo ngôn ngữ: hai rubric vi/en là hai dòng đời độc lập — không scope
        // thì sửa rubric VI làm nhảy số của rubric EN vốn chưa từng có phiên bản nào. An toàn vì Version
        // không nằm trong unique index, và answer_scores dedup theo (AnswerId, AttemptNo, RubricVersion)
        // mà một answer luôn thuộc đúng một session ⇒ đúng một ngôn ngữ.
        var maxVersion = await _db.RubricCriteria
            .Where(c => c.CampaignId == null && c.CandidateId == candidateId && c.JobCategory == jobCategory
                        && c.Language == lang)
            .Select(c => (int?)c.Version).MaxAsync(ct) ?? 0;
        var newVersion = maxVersion + 1;

        // ── SC2 — PHẠM VI CHẤM kế thừa từ BỘ CHUẨN cùng tên ────────────────────────────────────
        //
        // Trước SC2 khối `new RubricCriterion` dưới đây KHÔNG set `ScoringScope` ⇒ mọi tiêu chí rơi về
        // default của EF là `Always` (xem RubricCriterionConfiguration). Hệ quả đo trên production:
        //   • rubric riêng BA/vi có 7/7 dòng `Always`, BE/vi 9/9 `Always` — KHÔNG một dòng
        //     `WhenTargeted` nào, trong khi bộ seed cùng nghề là 4 `Always` (cách nói) + 3
        //     `WhenTargeted` (nội dung);
        //   • `PracticeService.LoadTargetableCriteriaAsync` lọc đúng `WhenTargeted` ⇒ trả RỖNG ⇒ nhánh
        //     `if (targetable.Count > 0)` trượt ⇒ gọi overload `GenerateQuestionsAsync` KHÔNG kèm
        //     criteria ⇒ AIService không gắn nhãn ⇒ `practice_questions.target_criterion_ids = null`;
        //   • đo được: 400/593 câu hỏi (67%) trắng nhãn, hỏng THEO CẢ BUỔI — 37/96 buổi (39%) trắng
        //     sạch, 0 buổi nửa nọ nửa kia, và chỉ đúng hai nghề có rubric riêng (BA/vi + BE/vi) dính,
        //     FE/vi và BA/en đạt 100%. Đúng chữ ký của một thuộc tính RUBRIC cố định suốt buổi, không
        //     phải lỗi ngẫu nhiên của mô hình.
        //   • triệu chứng cuối cùng người dùng thấy: câu đào sâu chỉ hỏi cơ chế xoay vòng refresh token
        //     vẫn bị chấm tiêu chí "Thiết kế hệ thống & CSDL" 2–3/5 — chính bộ chấm viết trong nhận xét
        //     rằng "câu trả lời tập trung vào cơ chế bảo mật hơn là thiết kế hệ thống tổng thể hay CSDL"
        //     rồi VẪN trừ điểm.
        //
        // Chú thích ở PracticeService (~dòng 310) đã lường ca này và coi là degrade chấp nhận được
        // ("rubric riêng BC16 chưa phân loại → không có gì để gắn nhãn"). Số đo nói ngược lại: nó không
        // hiếm, nó là 39% số buổi. Nên chỗ vá đúng là ở NGUỒN, không phải ở nhánh tiêu thụ.
        //
        // Vì sao kế thừa theo TÊN chạy được ở đây mà vẫn KHÔNG mâu thuẫn với cảnh báo "cấm khớp tên"
        // trên `RubricCriterion.ScoringScope`: cảnh báo đó cấm khớp tên ở ĐƯỜNG CHẤM (runtime, mọi
        // lượt, mọi ngôn ngữ). Còn đây là lúc GHI, khớp trong đúng một (nghề, ngôn ngữ) với bộ chuẩn
        // của chính ngôn ngữ đó, và khớp trượt chỉ rơi về `Always` = hành vi cũ. Kiểm trên prod: rubric
        // riêng dùng ĐÚNG 7 tên của bộ seed, trùng khít từng chữ — người dùng chép bộ seed
        // (`GetEffectiveAsync` trả seed làm template) rồi chỉ chỉnh trọng số + thang điểm.
        //
        // Chuẩn hoá NHẸ (trim + không phân biệt hoa thường), KHÔNG fuzzy: khớp SAI còn tệ hơn không
        // khớp — gán nhầm `WhenTargeted` cho một tiêu chí cách nói thì nó chỉ được chấm ở vài câu, và
        // không có triệu chứng nào ngoài điểm lặng lẽ đổi nghĩa.
        var seedScopes = await _db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == jobCategory && c.Language == lang && c.IsActive)
            .Select(c => new { c.Name, c.ScoringScope })
            .ToListAsync(ct);

        // `TryAdd` chứ không `ToDictionary`: unique index ux_rubric_criteria_b2c_default_version_name
        // đã cấm trùng tên trong một (nghề, ngôn ngữ, version), nhưng bộ chuẩn CÓ nhiều version và câu
        // trên lọc `IsActive` chứ không lọc version — một lần ghi bộ chuẩn hỏng nửa chừng là đủ để có
        // hai bản active. Ném `ArgumentException` ở đây sẽ thành 500 cho một thao tác lưu rubric vốn
        // không liên quan (tiền lệ F2b).
        var scopeByName = new Dictionary<string, ScoringScope>(StringComparer.OrdinalIgnoreCase);
        foreach (var seedRow in seedScopes) scopeByName.TryAdd(seedRow.Name.Trim(), seedRow.ScoringScope);

        var rows = normalized.Select(i => new RubricCriterion
        {
            Id = Guid.NewGuid(),
            Name = i.Name,
            Description = i.Description,
            Weight = i.Weight,
            MaxScore = i.MaxScore,
            IsActive = true,
            JobCategory = jobCategory,
            Language = lang,
            CampaignId = null,
            CandidateId = candidateId,
            Version = newVersion,
            // Trượt khớp ⇒ `Always` = ĐÚNG default cũ ⇒ ứng viên tự thêm tiêu chí lạ không bị đổi hành vi.
            ScoringScope = scopeByName.TryGetValue(i.Name, out var scope) ? scope : ScoringScope.Always,
            // Mốc điểm (E9). Rỗng = chưa khai ⇒ ScoringCriteriaBuilder sinh dải mặc định như trước.
            Levels = ValidateLevels(i.Name, i.MaxScore, i.Levels)
                .Select(l => new RubricLevel { Id = Guid.NewGuid(), Score = l.Score, Descriptor = l.Descriptor })
                .ToList(),
        }).ToList();

        // Tên KHÔNG khớp bộ chuẩn nào = ứng viên tự thêm tiêu chí mới. Giữ `Always` (hành vi AN TOÀN:
        // vẫn được chấm, chấm thừa chứ không bỏ sót) NHƯNG phải nói ra, vì cả hai chiều đều sai IM
        // LẶNG: là tiêu chí NỘI DUNG mà để `Always` thì nó bị chấm cho mọi câu (đúng lỗi SC2 này);
        // để `WhenTargeted` mà không câu nào nhắm tới thì nó không bao giờ được chấm và biến mất khỏi
        // kết quả. Không log thì không ai biết bộ tiêu chí của ai đang đo bằng thước nào.
        var unmatched = normalized.Where(i => !scopeByName.ContainsKey(i.Name)).Select(i => i.Name).ToList();
        if (unmatched.Count > 0 && scopeByName.Count == 0)
            _logger.LogWarning(
                "SC2: không tìm thấy BỘ CHUẨN active cho ({JobCategory}, {Language}) — toàn bộ "
                + "{Count} tiêu chí của rubric riêng (candidate {CandidateId}, version {Version}) giữ "
                + "ScoringScope=Always ⇒ câu hỏi buổi luyện sẽ KHÔNG được gắn nhãn tiêu chí. Nhiều khả "
                + "năng seed BC11 chưa apply trên môi trường này.",
                jobCategory, lang, unmatched.Count, candidateId, newVersion);
        else if (unmatched.Count > 0)
            _logger.LogWarning(
                "SC2: {Count} tiêu chí của rubric riêng (candidate {CandidateId}, {JobCategory}/{Language}, "
                + "version {Version}) không khớp tên nào trong bộ chuẩn nên giữ ScoringScope=Always: "
                + "{Names}. Nếu đây là tiêu chí NỘI DUNG thì nó sẽ bị chấm cho MỌI câu hỏi.",
                unmatched.Count, candidateId, jobCategory, lang, newVersion, string.Join(" · ", unmatched));

        _db.RubricCriteria.AddRange(rows);

        // 1 SaveChanges = 1 transaction: deactivate cũ + add mới atomic.
        await _db.SaveChangesAsync(ct);

        return new RubricResponse(jobCategory, IsCustom: true, rows.Select(Map).ToList());
    }

    public async Task ResetAsync(
        Guid candidateId, JobCategory jobCategory, string? language = null, CancellationToken ct = default)
    {
        var lang = ValidateLanguage(language);

        // Cùng lý do như ReplaceAsync: xoá rubric tiếng Anh KHÔNG được đụng rubric tiếng Việt.
        var current = await _db.RubricCriteria
            .Where(c => c.CampaignId == null && c.CandidateId == candidateId
                        && c.JobCategory == jobCategory && c.Language == lang && c.IsActive)
            .ToListAsync(ct);
        if (current.Count == 0) return;   // đã dùng mặc định → no-op (idempotent)
        foreach (var c in current) c.IsActive = false;
        await _db.SaveChangesAsync(ct);
    }

    // Bản sao của PracticeService.ValidateLanguage / RoadmapService.ValidateLanguage — giữ NGUYÊN luật,
    // không phát minh luật mới.
    // ⚠ Ném InvalidOperationException chứ KHÔNG phải ArgumentException: RubricController chỉ bắt
    // InvalidOperationException → 400; ArgumentException rơi xuống ống dẫn chung → 500 (Interview không
    // có exception handler toàn cục). Đây là lỗi đã xảy ra ở F2b.
    private string ValidateLanguage(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return "vi";
        var language = requested.Trim().ToLowerInvariant();
        if (language is not ("vi" or "en"))
            throw new InvalidOperationException("language chỉ nhận vi hoặc en.");
        if (!_bilingualEnabled && language != "vi")
            throw new InvalidOperationException("Bilingual interview chưa được bật.");
        return language;
    }

    // Validate + chuẩn hoá (mirror C12 CampaignService): name không rỗng/không trùng, maxScore≥1,
    // 0<weight≤1, Σweight∈[0.99,1.01] → chuẩn hoá Σ→1 (làm tròn 4 số khớp numeric(5,4)).
    private static List<RubricCriterionInput> NormalizeAndValidate(List<RubricCriterionInput>? inputs)
    {
        if (inputs is null || inputs.Count == 0)
            throw new InvalidOperationException("Rubric phải có ít nhất 1 tiêu chí.");

        foreach (var i in inputs)
        {
            if (string.IsNullOrWhiteSpace(i.Name))
                throw new InvalidOperationException("Tên tiêu chí không được rỗng.");
            if (i.MaxScore < 1)
                throw new InvalidOperationException($"maxScore của '{i.Name}' phải ≥ 1.");
            if (i.Weight <= 0m || i.Weight > 1m)
                throw new InvalidOperationException($"weight của '{i.Name}' phải trong khoảng (0, 1].");
        }

        var names = inputs.Select(i => i.Name.Trim().ToLowerInvariant()).ToList();
        if (names.Distinct().Count() != names.Count)
            throw new InvalidOperationException("Tên tiêu chí bị trùng.");

        var sum = inputs.Sum(i => i.Weight);
        if (sum < 0.99m || sum > 1.01m)
            throw new InvalidOperationException($"Tổng weight phải xấp xỉ 1 (hiện {sum:0.####}).");

        return inputs
            .Select(i => i with { Name = i.Name.Trim(), Weight = Math.Round(i.Weight / sum, 4) })
            .ToList();
    }

    /// <summary>
    /// Kiểm thang điểm bằng luật DÙNG CHUNG với B2B (<see cref="CriterionLevelRules"/>) — không viết
    /// luật thứ hai: thang méo KHÔNG làm lỗi nào nổ ở đường chấm, nó chỉ làm điểm sai, nên hai bản
    /// luật lệch nhau là hai kiểu chấm mà không có triệu chứng nào ngoài điểm số trông vẫn hợp lý.
    /// <c>null</c>/rỗng = chưa khai mốc, hợp lệ.
    /// </summary>
    private static IReadOnlyList<RubricLevelSnapshot> ValidateLevels(
        string criterionName, int maxScore, List<RubricLevelInput>? levels)
    {
        if (levels is null || levels.Count == 0) return [];

        var (error, normalized) = CriterionLevelRules.Validate(
            criterionName, maxScore,
            levels.Select(l => new RubricLevelSnapshot(l.Score, l.Descriptor)).ToList());

        // InvalidOperationException chứ KHÔNG phải ArgumentException — RubricController chỉ bắt loại
        // này → 400; ArgumentException rơi xuống ống dẫn chung → 500 (lỗi đã xảy ra ở F2b). Đây cũng
        // chính là lý do CriterionLevelRules TRẢ lỗi thay vì tự ném.
        if (error is not null) throw new InvalidOperationException(error);
        return normalized;
    }

    private static RubricCriterionItem Map(RubricCriterion c)
        => new(c.Id, c.Name, c.Description, c.Weight, c.MaxScore,
            // `.Include()` KHÔNG bảo đảm thứ tự — sắp ở đây thay vì tin vào DB.
            (c.Levels ?? []).OrderBy(l => l.Score)
                .Select(l => new RubricLevelInput(l.Score, l.Descriptor)).ToList());
}
