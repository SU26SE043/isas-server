using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.Shared.Rubric;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// Admin quản BỘ CHUẨN rubric B2C (<c>campaign_id IS NULL AND candidate_id IS NULL</c>).
///
/// <para><b>Vì sao có màn này:</b> bộ chuẩn là thước đo áp cho NHÓM ĐÔNG NHẤT (mọi người luyện tập
/// chưa khai rubric riêng) và cho tới nay nó không có một dòng mốc điểm nào — mọi lượt chấm rơi vào
/// dải mặc định, prompt in ra <c>• Mức 3: Mức 3/5</c> rồi bắt mô hình bám vào một chuỗi tautology.
/// Trước đây sửa một chữ mô tả cũng cần một migration + một lần deploy (tiền lệ
/// <c>SyncEnglishRubricDescriptions</c>); đây là chi phí mà màn này xoá bỏ.</para>
///
/// <para><b>Theo mẫu BC16 (<see cref="RubricLibraryService"/>), KHÔNG theo mẫu F21.</b> F21 override
/// được vì bản mặc định của nó là hằng số trong CODE và bảng chỉ chứa phần ghi đè. Ở đây bản mặc định
/// là DỮ LIỆU THẬT trong <c>rubric_criteria</c> mà đường chấm đọc thẳng ⇒ làm kiểu override thì mọi
/// call-site đọc rubric phải LEFT JOIN + merge, tức đẻ thêm một khe lệch giữa các bản sao mà
/// <see cref="RubricCriteriaLoader"/> vừa gom lại.</para>
/// </summary>
public interface IAdminB2CRubricService
{
    /// <summary>Ma trận trạng thái 3 nghề (× ngôn ngữ được lọc) — trả lời "còn thiếu mốc ở đâu".</summary>
    Task<IReadOnlyList<AdminRubricMatrixRow>> GetMatrixAsync(string? language, CancellationToken ct = default);

    /// <summary>Bộ đang hiệu lực của 1 (nghề, ngôn ngữ). <c>null</c> = chưa có bộ nào (seed chưa apply).</summary>
    Task<AdminRubricResponse?> GetAsync(JobCategory jobCategory, string? language, CancellationToken ct = default);

    /// <summary>Lưu nội dung mới. Không khác gì bản đang chạy ⇒ KHÔNG bump, trả <c>Changed = false</c>.</summary>
    Task<AdminRubricResponse?> ReplaceAsync(
        JobCategory jobCategory, UpsertAdminRubricRequest request, string? language, CancellationToken ct = default);

    /// <summary>Quay về nội dung gốc trong code (mốc rỗng ⇒ dải mặc định) bằng cách THÊM phiên bản mới.</summary>
    Task<AdminRubricResponse?> ResetAsync(JobCategory jobCategory, string? language, CancellationToken ct = default);

    Task<IReadOnlyList<AdminRubricVersionItem>> HistoryAsync(
        JobCategory jobCategory, string? language, CancellationToken ct = default);
}

public class AdminB2CRubricService(InterviewDbContext db) : IAdminB2CRubricService
{
    private static readonly JobCategory[] AllCategories = [JobCategory.BA, JobCategory.BE, JobCategory.FE];

    public async Task<IReadOnlyList<AdminRubricMatrixRow>> GetMatrixAsync(
        string? language, CancellationToken ct = default)
    {
        var languages = language is null
            ? new[] { "vi", "en" }
            : [ValidateLanguage(language)];

        var rows = new List<AdminRubricMatrixRow>();
        foreach (var lang in languages)
        {
            foreach (var cat in AllCategories)
            {
                var criteria = await ActiveSetQuery(cat, lang).Include(c => c.Levels).ToListAsync(ct);
                rows.Add(new AdminRubricMatrixRow(
                    cat, lang,
                    Version: criteria.Count > 0 ? criteria[0].Version : 0,
                    CriteriaCount: criteria.Count,
                    WithLevelsCount: criteria.Count(c => c.Levels.Count > 0)));
            }
        }
        return rows;
    }

    public async Task<AdminRubricResponse?> GetAsync(
        JobCategory jobCategory, string? language, CancellationToken ct = default)
    {
        var lang = ValidateLanguage(language);
        var criteria = await LoadActiveSetAsync(jobCategory, lang, ct);
        return criteria.Count == 0 ? null : Respond(jobCategory, lang, criteria, changed: false);
    }

    public async Task<AdminRubricResponse?> ReplaceAsync(
        JobCategory jobCategory, UpsertAdminRubricRequest request, string? language, CancellationToken ct = default)
    {
        var lang = ValidateLanguage(language);
        var current = await LoadActiveSetAsync(jobCategory, lang, ct, tracking: true);
        if (current.Count == 0) return null;

        var inputs = request?.Criteria ?? throw new InvalidOperationException("Thiếu danh sách tiêu chí.");

        // Replace-all NỘI DUNG: phải gửi đủ và đúng tập tiêu chí đang có. Cho gửi thiếu thì tiêu chí
        // vắng mặt sẽ âm thầm giữ nội dung cũ trong khi admin tin là mình đã xoá mốc của nó.
        var byId = new Dictionary<Guid, AdminRubricCriterionInput>();
        foreach (var i in inputs)
        {
            if (!byId.TryAdd(i.Id, i))
                throw new InvalidOperationException($"Tiêu chí {i.Id} bị gửi trùng.");
        }

        var currentIds = current.Select(c => c.Id).ToHashSet();
        var unknown = byId.Keys.FirstOrDefault(id => !currentIds.Contains(id));
        if (unknown != Guid.Empty)
            throw new InvalidOperationException($"Tiêu chí {unknown} không thuộc bộ chuẩn {jobCategory} ({lang}).");
        var missing = current.FirstOrDefault(c => !byId.ContainsKey(c.Id));
        if (missing is not null)
            throw new InvalidOperationException($"Thiếu tiêu chí '{missing.Name}' trong payload.");

        // Dựng nội dung MỚI: bốn trường ngoài quyền admin chép NGUYÊN từ bản cũ (xem AdminRubricCriterionInput).
        var proposed = new List<(RubricCriterion Source, string? Description, IReadOnlyList<RubricLevelSnapshot> Levels)>();
        foreach (var c in current)
        {
            var input = byId[c.Id];
            var levels = ValidateLevels(c.Name, c.MaxScore, input.Levels);
            proposed.Add((c, string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(), levels));
        }

        // KHÔNG bump khi không đổi gì. Vân tay dùng chung với B2B (Isas.Shared) nên hai bên không thể
        // trả lời khác nhau cho câu "có thật sự đổi thước đo không".
        var before = RubricFingerprint.Compute(current.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select((c, i) => Snapshot(i, c.Name, c.Description, c.Weight, c.MaxScore,
                c.Levels.OrderBy(l => l.Score).Select(l => new RubricLevelSnapshot(l.Score, l.Descriptor)).ToList())));
        var after = RubricFingerprint.Compute(proposed.OrderBy(p => p.Source.Name, StringComparer.Ordinal)
            .Select((p, i) => Snapshot(i, p.Source.Name, p.Description, p.Source.Weight, p.Source.MaxScore, p.Levels)));

        if (before == after)
            return Respond(jobCategory, lang, current, changed: false);

        var rows = await AppendVersionAsync(jobCategory, lang, current,
            proposed.Select(p => (p.Source.Name, p.Description, p.Source.Weight, p.Source.MaxScore,
                                  p.Source.ScoringScope, p.Levels)).ToList(), ct);
        return Respond(jobCategory, lang, rows, changed: true);
    }

    public async Task<AdminRubricResponse?> ResetAsync(
        JobCategory jobCategory, string? language, CancellationToken ct = default)
    {
        var lang = ValidateLanguage(language);
        var current = await LoadActiveSetAsync(jobCategory, lang, ct, tracking: true);
        if (current.Count == 0) return null;

        // Nội dung gốc lấy từ CHÍNH nguồn seed trong code — không chép tay một bản thứ hai.
        // `Levels` rỗng ⇒ quay về dải mặc định, đúng nghĩa "về gốc".
        var seed = B2CRubricSeed.Build()
            .Where(c => c.JobCategory == jobCategory && c.Language == lang)
            .ToList();
        if (seed.Count == 0)
            throw new InvalidOperationException($"Không có bộ gốc cho {jobCategory} ({lang}).");

        var before = RubricFingerprint.Compute(current.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select((c, i) => Snapshot(i, c.Name, c.Description, c.Weight, c.MaxScore,
                c.Levels.OrderBy(l => l.Score).Select(l => new RubricLevelSnapshot(l.Score, l.Descriptor)).ToList())));
        var after = RubricFingerprint.Compute(seed.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select((c, i) => Snapshot(i, c.Name, c.Description, c.Weight, c.MaxScore, [])));

        if (before == after)
            return Respond(jobCategory, lang, current, changed: false);

        // Append phiên bản mới, KHÔNG bật lại `is_active` của v1: append-only giữ cho câu hỏi "đang là
        // phiên bản mấy" luôn có đúng một câu trả lời, và giữ nguyên dấu vết ai từng dùng bản nào.
        var rows = await AppendVersionAsync(jobCategory, lang, current,
            seed.Select(c => (c.Name, c.Description, c.Weight, c.MaxScore, c.ScoringScope,
                              (IReadOnlyList<RubricLevelSnapshot>)[])).ToList(), ct);
        return Respond(jobCategory, lang, rows, changed: true);
    }

    public async Task<IReadOnlyList<AdminRubricVersionItem>> HistoryAsync(
        JobCategory jobCategory, string? language, CancellationToken ct = default)
    {
        var lang = ValidateLanguage(language);
        var all = await db.RubricCriteria.AsNoTracking().Include(c => c.Levels)
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == jobCategory && c.Language == lang)
            .ToListAsync(ct);

        return all
            .GroupBy(c => c.Version)
            .OrderByDescending(g => g.Key)
            .Select(g => new AdminRubricVersionItem(
                g.Key,
                IsActive: g.Any(c => c.IsActive),
                CriteriaCount: g.Count(),
                WithLevelsCount: g.Count(c => c.Levels.Count > 0)))
            .ToList();
    }

    // ── nội bộ ─────────────────────────────────────────────────────────────────────────────────

    private IQueryable<RubricCriterion> ActiveSetQuery(JobCategory jobCategory, string language)
        => db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == jobCategory && c.Language == language && c.IsActive);

    private async Task<List<RubricCriterion>> LoadActiveSetAsync(
        JobCategory jobCategory, string language, CancellationToken ct, bool tracking = false)
    {
        var query = tracking
            ? db.RubricCriteria.Where(c => c.CampaignId == null && c.CandidateId == null
                                           && c.JobCategory == jobCategory && c.Language == language && c.IsActive)
            : ActiveSetQuery(jobCategory, language);
        return await query.Include(c => c.Levels).OrderBy(c => c.Name).ToListAsync(ct);
    }

    /// <summary>
    /// Hạ cờ bộ đang chạy + chèn bộ mới với <c>Version = max + 1</c>, trong MỘT <c>SaveChanges</c>.
    ///
    /// <para>Một <c>SaveChanges</c> = một transaction ngầm ⇒ không có khoảnh khắc nào tồn tại hai bộ
    /// active (hoặc không bộ nào). Cố ý KHÔNG tự mở transaction: <c>DbRetry</c> chỉ cần cho khối tự
    /// mở, còn ở đây execution strategy của EF đã bọc sẵn lời gọi này.</para>
    ///
    /// <para>⚠ <c>max(version)</c> KHÔNG phải trọng tài cho hai admin bấm Lưu cùng lúc — thứ chặn được
    /// là unique <c>ux_rubric_criteria_b2c_default_version_name</c>; bên thua nhận
    /// <c>DbUpdateException</c> và không ghi được gì.</para>
    /// </summary>
    private async Task<List<RubricCriterion>> AppendVersionAsync(
        JobCategory jobCategory, string language, List<RubricCriterion> current,
        List<(string Name, string? Description, decimal Weight, int MaxScore, ScoringScope Scope,
              IReadOnlyList<RubricLevelSnapshot> Levels)> content,
        CancellationToken ct)
    {
        foreach (var c in current) c.IsActive = false;

        var maxVersion = await db.RubricCriteria
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == jobCategory && c.Language == language)
            .Select(c => (int?)c.Version).MaxAsync(ct) ?? 0;
        var newVersion = maxVersion + 1;

        var rows = content.Select(x => new RubricCriterion
        {
            Id = Guid.NewGuid(),
            Name = x.Name,
            Description = x.Description,
            Weight = x.Weight,
            MaxScore = x.MaxScore,
            ScoringScope = x.Scope,
            IsActive = true,
            JobCategory = jobCategory,
            Language = language,
            CampaignId = null,
            CandidateId = null,
            Version = newVersion,
            Levels = x.Levels
                .Select(l => new RubricLevel { Id = Guid.NewGuid(), Score = l.Score, Descriptor = l.Descriptor })
                .ToList()
        }).ToList();

        db.RubricCriteria.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.OrderBy(c => c.Name).ToList();
    }

    private static RubricCriterionSnapshot Snapshot(
        int orderNo, string name, string? description, decimal weight, int maxScore,
        IReadOnlyList<RubricLevelSnapshot> levels)
        => new(orderNo, name, description, weight, maxScore, levels);

    /// <summary>
    /// Kiểm thang điểm bằng luật DÙNG CHUNG (<see cref="CriterionLevelRules"/>), không viết luật thứ hai.
    /// <c>null</c>/rỗng = chưa khai mốc, hợp lệ.
    /// </summary>
    private static IReadOnlyList<RubricLevelSnapshot> ValidateLevels(
        string criterionName, int maxScore, List<AdminRubricLevelInput>? levels)
    {
        if (levels is null || levels.Count == 0) return [];

        var (error, normalized) = CriterionLevelRules.Validate(
            criterionName, maxScore,
            levels.Select(l => new RubricLevelSnapshot(l.Score, l.Descriptor)).ToList());

        // Ném InvalidOperationException chứ KHÔNG phải ArgumentException: controller của Interview chỉ
        // bắt loại này → 400; ArgumentException rơi xuống ống dẫn chung → 500 với MỌI input sai (lỗi
        // đã xảy ra ở F2b). Đây cũng chính là lý do CriterionLevelRules TRẢ lỗi thay vì tự ném.
        if (error is not null) throw new InvalidOperationException(error);
        return normalized;
    }

    /// <summary>
    /// Chỉ kiểm tập ngôn ngữ, CỐ Ý không gate theo <c>Interview:Bilingual:Enabled</c> như đường của
    /// người luyện: admin phải soạn được bộ tiếng Anh TRƯỚC khi bật cờ song ngữ, giống như phải nạp
    /// corpus trước khi bật grounding. Gate ở đây sẽ buộc bật cờ rồi mới soạn — tức bật một tính năng
    /// mà thước đo của nó còn rỗng.
    /// </summary>
    private static string ValidateLanguage(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return "vi";
        var language = requested.Trim().ToLowerInvariant();
        if (language is not ("vi" or "en"))
            throw new InvalidOperationException("language chỉ nhận vi hoặc en.");
        return language;
    }

    private static AdminRubricResponse Respond(
        JobCategory jobCategory, string language, List<RubricCriterion> criteria, bool changed)
        => new(jobCategory, language,
            Version: criteria.Count > 0 ? criteria[0].Version : 0,
            Changed: changed,
            SampleQuestions: AdminPreviewQuestionBank.For(jobCategory, language)
                .Select(q => new AdminSampleQuestionItem(q.Id, q.Text)).ToList(),
            Criteria: criteria.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => new AdminRubricCriterionItem(
                c.Id, c.Name, c.Description, c.Weight, c.MaxScore, c.ScoringScope.ToString(),
                // `.Include()` KHÔNG bảo đảm thứ tự — sắp ở đây thay vì tin vào DB, nếu không mốc hiện
                // lộn xộn trên Postgres mà vẫn đúng thứ tự trên SQLite (test).
                c.Levels.OrderBy(l => l.Score).Select(l => new AdminRubricLevelItem(l.Score, l.Descriptor)).ToList()))
                .ToList());
}
