using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// BC16 — Rubric CÁ NHÂN B2C: candidate tự quản bộ tiêu chí luyện tập theo <see cref="JobCategory"/>
/// (không admin). Chưa khai → dùng seed mặc định (BC11). Scoring resolve ưu-tiên-riêng-else-mặc-định
/// qua <see cref="B2CRubricScope"/>. Điểm tổng vẫn TB cộng (INT-10) — weight chỉ để hiển thị.
/// </summary>
public interface IRubricLibraryService
{
    /// <summary>Rubric HIỆU LỰC cho 1 nghề: rubric riêng nếu có active, else seed mặc định (template).</summary>
    Task<RubricResponse> GetEffectiveAsync(Guid candidateId, JobCategory jobCategory, CancellationToken ct = default);

    /// <summary>Thay TOÀN BỘ rubric riêng cho 1 nghề (replace-all, soft-versioned, FK-safe).</summary>
    Task<RubricResponse> ReplaceAsync(
        Guid candidateId, JobCategory jobCategory, UpsertRubricRequest request, CancellationToken ct = default);

    /// <summary>Xoá rubric riêng → quay về seed mặc định (soft-deactivate, idempotent).</summary>
    Task ResetAsync(Guid candidateId, JobCategory jobCategory, CancellationToken ct = default);
}

public class RubricLibraryService : IRubricLibraryService
{
    private readonly InterviewDbContext _db;

    public RubricLibraryService(InterviewDbContext db) => _db = db;

    public async Task<RubricResponse> GetEffectiveAsync(
        Guid candidateId, JobCategory jobCategory, CancellationToken ct = default)
    {
        // TODO(Q9): service này CHƯA có khái niệm ngôn ngữ — cả 6 query bên dưới lẫn `ReplaceAsync`
        // (không set `Language` ⇒ mọi rubric riêng rơi về "vi") đều đơn ngữ. "vi" viết TƯỜNG MINH ở đây
        // thay vì ẩn trong overload mặc định, để chỗ cần sửa nhìn thấy được. Hệ quả đang sống: GET trả
        // 14 tiêu chí Σweight=2.0 và PUT chính payload đó → 400. Sửa = đổi hợp đồng API, cần lockstep FE.
        var owner = await B2CRubricScope.ResolveOwnerAsync(_db, candidateId, jobCategory, "vi", ct);
        if (owner is Guid oid)
        {
            var custom = await _db.RubricCriteria.AsNoTracking()
                .Where(c => c.CampaignId == null && c.CandidateId == oid && c.JobCategory == jobCategory && c.IsActive)
                .OrderByDescending(c => c.Weight).ThenBy(c => c.Name)
                .ToListAsync(ct);
            return new RubricResponse(jobCategory, IsCustom: true, custom.Select(Map).ToList());
        }

        // Chưa có rubric riêng → seed mặc định làm template (FE clone rồi sửa).
        var seed = await _db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == jobCategory && c.IsActive)
            .OrderByDescending(c => c.Weight).ThenBy(c => c.Name)
            .ToListAsync(ct);
        return new RubricResponse(jobCategory, IsCustom: false, seed.Select(Map).ToList());
    }

    public async Task<RubricResponse> ReplaceAsync(
        Guid candidateId, JobCategory jobCategory, UpsertRubricRequest request, CancellationToken ct = default)
    {
        var normalized = NormalizeAndValidate(request?.Criteria);   // throw InvalidOperationException → 400

        // FK-safe: KHÔNG hard-delete (answer_scores cũ có thể trỏ vào — FK Restrict). Soft-deactivate
        // bản active hiện tại + thêm bản mới active (Version++). Scoring luôn lọc IsActive → chọn bản mới.
        var current = await _db.RubricCriteria
            .Where(c => c.CampaignId == null && c.CandidateId == candidateId
                        && c.JobCategory == jobCategory && c.IsActive)
            .ToListAsync(ct);
        foreach (var c in current) c.IsActive = false;

        var maxVersion = await _db.RubricCriteria
            .Where(c => c.CampaignId == null && c.CandidateId == candidateId && c.JobCategory == jobCategory)
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
            CampaignId = null,
            CandidateId = candidateId,
            Version = newVersion,
        }).ToList();
        _db.RubricCriteria.AddRange(rows);

        // 1 SaveChanges = 1 transaction: deactivate cũ + add mới atomic.
        await _db.SaveChangesAsync(ct);

        return new RubricResponse(jobCategory, IsCustom: true, rows.Select(Map).ToList());
    }

    public async Task ResetAsync(Guid candidateId, JobCategory jobCategory, CancellationToken ct = default)
    {
        var current = await _db.RubricCriteria
            .Where(c => c.CampaignId == null && c.CandidateId == candidateId
                        && c.JobCategory == jobCategory && c.IsActive)
            .ToListAsync(ct);
        if (current.Count == 0) return;   // đã dùng mặc định → no-op (idempotent)
        foreach (var c in current) c.IsActive = false;
        await _db.SaveChangesAsync(ct);
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

    private static RubricCriterionItem Map(RubricCriterion c)
        => new(c.Id, c.Name, c.Description, c.Weight, c.MaxScore);
}
