using System.Globalization;
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

    /// <summary>
    /// Lưu nội dung mới (RUB1: thêm/xoá/đổi tên/trọng số/phạm vi/mô tả/mốc). Không khác gì bản đang chạy
    /// ⇒ KHÔNG bump, trả <c>Changed = false</c>. Tiêu chí vắng khỏi body = bị xoá khỏi phiên bản mới.
    /// </summary>
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
                // Chỉ đếm tiêu chí CẦN mốc (AI chấm). Tiêu chí đo bằng số đo (F11) cố ý 0 mốc — đếm nó
                // vào mẫu số là ma trận báo "6/7 · thiếu mốc" vĩnh viễn cho một thứ không cần (prod 2026-09-16).
                var needLevels = criteria.Where(c => c.ScoringMethod == CriterionScoringMethod.Ai).ToList();
                rows.Add(new AdminRubricMatrixRow(
                    cat, lang,
                    Version: criteria.Count > 0 ? criteria[0].Version : 0,
                    CriteriaCount: needLevels.Count,
                    WithLevelsCount: needLevels.Count(c => c.Levels.Count > 0)));
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

        // RUB1 — admin sửa ĐỦ bộ chuẩn: thêm/xoá/đổi tên/đổi trọng số/đổi phạm vi. Tiêu chí đang có mà
        // vắng khỏi body = bị XOÁ khỏi phiên bản mới (bản cũ vẫn còn nguyên để chấm nốt các buổi đã ghim).
        //
        // ⚠ Hệ quả phải biết (cố ý, không phải lỗi):
        //   • ĐỔI TÊN — BC12 (điểm yếu → lộ trình), BC15 (đo cải thiện), F14 (mốc so với người khác) đều gom
        //     theo TÊN ⇒ lịch sử tiến bộ của tiêu chí đó BẮT ĐẦU LẠI từ phiên bản này. Rubric riêng (BC16)
        //     chép tên cũ cũng thôi kế thừa scope/method theo tên ở lần lưu sau (rơi về Always/Ai + log SC2).
        //   • THÊM/XOÁ tiêu chí `WhenTargeted` đổi `PracticeService.ComputeSeedCount` (sàn số câu gốc = số
        //     tiêu chí nội dung) ⇒ buổi mới có nhiều/ít câu gốc hơn và ít/nhiều khe đào sâu hơn.
        var currentById = current.ToDictionary(c => c.Id);
        var seenIds = new HashSet<Guid>();
        var proposed = new List<ProposedCriterion>(inputs.Count);

        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index]
                ?? throw new InvalidOperationException($"Tiêu chí ở vị trí {index + 1} rỗng.");

            if (input.Id is Guid id)
            {
                if (!seenIds.Add(id))
                    throw new InvalidOperationException($"Tiêu chí {id} bị gửi trùng.");
                if (!currentById.TryGetValue(id, out var source))
                    throw new InvalidOperationException(
                        $"Tiêu chí {id} không thuộc bộ chuẩn đang hiệu lực của {jobCategory} ({lang}).");

                // `null` = GIỮ NGUYÊN (không phải "ghi đè thành rỗng").
                var name = input.Name is null ? source.Name : NormalizeName(input.Name);
                var scope = input.ScoringScope is null ? source.ScoringScope : ParseScope(name, input.ScoringScope);

                // Tiêu chí đo bằng SỐ ĐO giọng nói (F11): được giữ/bỏ, được sửa mô tả/trọng số/mốc — nhưng
                // KHÔNG đổi tên (tên là thứ người luyện đọc để hiểu đó là chỉ số đo, và rubric riêng kế thừa
                // `ScoringMethod` theo tên) và KHÔNG đổi phạm vi (nó luôn được đo ở mọi câu có ghi âm).
                if (source.ScoringMethod == CriterionScoringMethod.DeliveryMetrics)
                {
                    if (!string.Equals(name, source.Name, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Tiêu chí '{source.Name}' do hệ thống tự đo từ giọng nói — không được đổi tên.");
                    if (scope != source.ScoringScope)
                        throw new InvalidOperationException(
                            $"Tiêu chí '{source.Name}' do hệ thống tự đo từ giọng nói — không được đổi phạm vi chấm.");
                }

                proposed.Add(new ProposedCriterion(
                    name, NormalizeDescription(input.Description),
                    NormalizeWeight(name, input.Weight ?? source.Weight),
                    source.MaxScore, scope,
                    // 🔴 Bản mới MANG THEO nguồn điểm của bản cũ — thiếu thì tiêu chí đo-bằng-số-đo âm thầm
                    // chuyển sang cho LLM chấm (đã xảy ra trên prod 14/09, xem AppendVersionAsync).
                    source.ScoringMethod,
                    ValidateLevels(name, source.MaxScore, input.Levels)));
            }
            else
            {
                // Tiêu chí MỚI: bắt buộc tên + trọng số + phạm vi. Luôn `Ai` + thang 5 (admin không chọn được).
                var label = $"Tiêu chí mới ở vị trí {index + 1}";
                if (input.Name is null)
                    throw new InvalidOperationException($"{label} thiếu tên.");
                var name = NormalizeName(input.Name);
                if (input.Weight is null)
                    throw new InvalidOperationException($"Tiêu chí mới '{name}' thiếu trọng số.");
                if (input.ScoringScope is null)
                    throw new InvalidOperationException(
                        $"Tiêu chí mới '{name}' thiếu phạm vi chấm (Always hoặc WhenTargeted).");

                proposed.Add(new ProposedCriterion(
                    name, NormalizeDescription(input.Description),
                    NormalizeWeight(name, input.Weight.Value),
                    NewCriterionMaxScore, ParseScope(name, input.ScoringScope),
                    CriterionScoringMethod.Ai,
                    ValidateLevels(name, NewCriterionMaxScore, input.Levels)));
            }
        }

        // Tên không trùng — trim, KHÔNG phân biệt hoa thường (chặt hơn unique index của DB, vốn phân biệt).
        var duplicate = proposed
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Tên tiêu chí '{duplicate.Key}' bị trùng (không phân biệt hoa thường).");

        // Phải còn ít nhất một tiêu chí do AI chấm: tiêu chí đo-bằng-số-đo bị loại khỏi điểm khi người
        // luyện nói dưới sàn (DeliveryFluencyScorer) ⇒ bộ chỉ còn nó thì một buổi có thể không có điểm nào.
        if (!proposed.Any(p => p.Method == CriterionScoringMethod.Ai))
            throw new InvalidOperationException(
                "Bộ chuẩn phải còn ít nhất một tiêu chí do AI chấm — tiêu chí hệ thống tự đo không đủ để chấm một buổi.");

        // Σweight trên ĐÚNG giá trị sẽ được lưu (đã làm tròn 4 chữ số — numeric(5,4)) và KHÔNG tự chuẩn
        // hoá: buổi B2C mới tính điểm CÓ TRỌNG SỐ (INT-10), nên con số admin nhìn thấy phải là con số
        // dùng để chấm. Kiểm trên giá trị chưa làm tròn thì 7 × (1/7) qua được nhưng lưu ra Σ = 1.0003.
        var sum = proposed.Sum(p => p.Weight);
        if (Math.Abs(sum - 1m) > WeightSumTolerance)
            throw new InvalidOperationException(
                $"Tổng trọng số phải bằng 1 (hiện {sum.ToString("0.####", CultureInfo.InvariantCulture)}) — "
                + "hệ thống không tự chuẩn hoá, hãy chỉnh lại trọng số các tiêu chí.");

        // KHÔNG bump khi không đổi gì. Vân tay dùng chung với B2B (Isas.Shared) nên hai bên không thể
        // trả lời khác nhau cho câu "có thật sự đổi thước đo không". Vân tay gồm tên · mô tả · trọng số ·
        // thang · PHẠM VI · mốc, và tập tiêu chí (thêm/xoá đổi danh sách) — thiếu phạm vi thì đổi
        // Always↔WhenTargeted sẽ không bump, mà đó là đổi mẫu số điểm (INT-18).
        var before = FingerprintOf(current);
        var after = RubricFingerprint.Compute(proposed.OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select((p, i) => Snapshot(i, p.Name, p.Description, p.Weight, p.MaxScore, p.Levels, p.Scope)));

        if (before == after)
            return Respond(jobCategory, lang, current, changed: false);

        var rows = await AppendVersionAsync(jobCategory, lang, current,
            proposed.Select(p => (p.Name, p.Description, p.Weight, p.MaxScore, p.Scope, p.Method, p.Levels)).ToList(),
            ct);
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

        var before = FingerprintOf(current);
        var after = RubricFingerprint.Compute(seed.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select((c, i) => Snapshot(i, c.Name, c.Description, c.Weight, c.MaxScore, [], c.ScoringScope)));

        if (before == after)
            return Respond(jobCategory, lang, current, changed: false);

        // Append phiên bản mới, KHÔNG bật lại `is_active` của v1: append-only giữ cho câu hỏi "đang là
        // phiên bản mấy" luôn có đúng một câu trả lời, và giữ nguyên dấu vết ai từng dùng bản nào.
        var rows = await AppendVersionAsync(jobCategory, lang, current,
            seed.Select(c => (c.Name, c.Description, c.Weight, c.MaxScore, c.ScoringScope,
                              c.ScoringMethod, (IReadOnlyList<RubricLevelSnapshot>)[])).ToList(), ct);
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
                // Cùng luật với ma trận: chỉ tiêu chí AI chấm mới CẦN mốc.
                CriteriaCount: g.Count(c => c.ScoringMethod == CriterionScoringMethod.Ai),
                WithLevelsCount: g.Count(c => c.ScoringMethod == CriterionScoringMethod.Ai && c.Levels.Count > 0)))
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
              CriterionScoringMethod Method, IReadOnlyList<RubricLevelSnapshot> Levels)> content,
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
            // 🔴 PHẢI chép: thiếu dòng này thì phiên bản mới rơi về mặc định `Ai`, tức MỘT LẦN admin
            // sửa mốc là tiêu chí chấm-bằng-SỐ-ĐO (Độ trôi chảy) âm thầm chuyển sang cho LLM chấm —
            // trong khi mô tả của chính nó nói ngược lại, và LLM KHÔNG có tín hiệu nào (không cao độ,
            // không số đo dừng trong prompt) nên nó sẽ bịa. Admin không đổi được cột này (BC-8/RUB1:
            // không có trong DTO; tiêu chí mới luôn `Ai`), nên nó phải đi theo bản cũ chứ không nhận
            // mặc định. Đã xảy ra trên prod 14/09: bản v2 chép tay mất cột này ⇒ "Độ trôi chảy" bị LLM chấm.
            ScoringMethod = x.Method,
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
        IReadOnlyList<RubricLevelSnapshot> levels, ScoringScope scope)
        => new(orderNo, name, description, weight, maxScore, levels, scope.ToString());

    private static string FingerprintOf(IEnumerable<RubricCriterion> criteria)
        => RubricFingerprint.Compute(criteria.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select((c, i) => Snapshot(i, c.Name, c.Description, c.Weight, c.MaxScore,
                c.Levels.OrderBy(l => l.Score).Select(l => new RubricLevelSnapshot(l.Score, l.Descriptor)).ToList(),
                c.ScoringScope)));

    /// <summary>Thang cố định của bộ chuẩn B2C (RUB1: admin không đổi được maxScore).</summary>
    private const int NewCriterionMaxScore = 5;

    /// <summary>Tên tối đa 100 ký tự (cột 128 — chừa đệm; tên dài hơn thì vỡ layout radar/bảng kết quả).</summary>
    private const int NameMaxLength = 100;

    /// <summary>|Σweight − 1| cho phép — đúng 1 đơn vị của chữ số thứ tư (numeric(5,4)).</summary>
    private const decimal WeightSumTolerance = 0.0001m;

    private sealed record ProposedCriterion(
        string Name, string? Description, decimal Weight, int MaxScore, ScoringScope Scope,
        CriterionScoringMethod Method, IReadOnlyList<RubricLevelSnapshot> Levels);

    private static string NormalizeName(string raw)
    {
        var name = raw.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Tên tiêu chí không được rỗng.");
        if (name.Length > NameMaxLength)
            throw new InvalidOperationException(
                $"Tên tiêu chí '{name[..40]}…' dài {name.Length} ký tự — tối đa {NameMaxLength}.");
        return name;
    }

    private static string? NormalizeDescription(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>
    /// Làm tròn 4 chữ số TRƯỚC khi kiểm — đúng giá trị Postgres sẽ lưu vào numeric(5,4) (làm tròn nửa
    /// lên, xa số 0). Kiểm trên giá trị thô thì 0.00004 qua được "&gt; 0" rồi lưu ra 0.0000 và nổ CHECK
    /// <c>ck_rubric_criteria_weight_range</c> ⇒ 500 thay vì 400.
    /// </summary>
    private static decimal NormalizeWeight(string name, decimal raw)
    {
        var weight = Math.Round(raw, 4, MidpointRounding.AwayFromZero);
        if (weight <= 0m || weight > 1m)
            throw new InvalidOperationException(
                $"Trọng số của '{name}' phải trong khoảng (0, 1] sau khi làm tròn 4 chữ số "
                + $"(hiện {weight.ToString("0.####", CultureInfo.InvariantCulture)}).");
        return weight;
    }

    /// <summary>
    /// Chỉ nhận đúng TÊN enum. <c>Enum.TryParse</c> trần nhận cả chuỗi số ("0", "7") ⇒ phạm vi rác lọt
    /// xuống DB và nổ CHECK thành 500.
    /// </summary>
    private static ScoringScope ParseScope(string name, string raw)
        => raw.Trim() switch
        {
            var v when v.Equals(nameof(ScoringScope.Always), StringComparison.OrdinalIgnoreCase) => ScoringScope.Always,
            var v when v.Equals(nameof(ScoringScope.WhenTargeted), StringComparison.OrdinalIgnoreCase) => ScoringScope.WhenTargeted,
            _ => throw new InvalidOperationException(
                $"Phạm vi chấm của '{name}' chỉ nhận Always hoặc WhenTargeted (hiện '{raw}').")
        };

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
                c.Id, c.Name, c.Description, c.Weight, c.MaxScore, c.ScoringScope.ToString(), c.ScoringMethod.ToString(),
                // `.Include()` KHÔNG bảo đảm thứ tự — sắp ở đây thay vì tin vào DB, nếu không mốc hiện
                // lộn xộn trên Postgres mà vẫn đúng thứ tự trên SQLite (test).
                c.Levels.OrderBy(l => l.Score).Select(l => new AdminRubricLevelItem(l.Score, l.Descriptor)).ToList()))
                .ToList());
}
