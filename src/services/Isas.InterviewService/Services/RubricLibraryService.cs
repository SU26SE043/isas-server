using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.Shared.Rubric;
using Microsoft.EntityFrameworkCore;

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

    // `config` optional (mẫu RoadmapService): DI tự resolve IConfiguration, còn test dựng thẳng
    // `new RubricLibraryService(db)` vẫn compile và chạy ở chế độ đơn ngữ như trước.
    public RubricLibraryService(InterviewDbContext db, IConfiguration? config = null)
    {
        _db = db;
        _bilingualEnabled = bool.TryParse(config?["Interview:Bilingual:Enabled"], out var bilingual) && bilingual;
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
            // Mốc điểm (E9). Rỗng = chưa khai ⇒ ScoringCriteriaBuilder sinh dải mặc định như trước.
            Levels = ValidateLevels(i.Name, i.MaxScore, i.Levels)
                .Select(l => new RubricLevel { Id = Guid.NewGuid(), Score = l.Score, Descriptor = l.Descriptor })
                .ToList(),
        }).ToList();
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
