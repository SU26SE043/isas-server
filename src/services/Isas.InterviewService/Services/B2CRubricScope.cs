using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// BC16 — Chọn "chủ" rubric B2C cho 1 (candidate, nghề): ưu tiên rubric RIÊNG của candidate, else
/// seed mặc định dùng chung (<c>candidate_id IS NULL</c>).
///
/// Dùng CHUNG cho cả 4 chỗ chọn tiêu chí B2C (publish/upload · callback guard · republisher · breakdown BC9)
/// để publish-time và callback-time luôn chọn CÙNG bộ tiêu chí — không lệch (E8 drop/clamp sai / thiếu
/// tiêu chí → Failed INT-9). B2B (<c>campaign_id != null</c>) KHÔNG dùng resolver này.
/// </summary>
public static class B2CRubricScope
{
    public static Task<Guid?> ResolveOwnerAsync(
        InterviewDbContext db, Guid candidateId, JobCategory jobCategory, CancellationToken ct = default)
        => ResolveOwnerAsync(db, candidateId, jobCategory, "vi", ct);

    /// <summary>
    /// Trả về <c>candidateId</c> nếu candidate có ≥1 tiêu chí RIÊNG đang active cho nghề đó,
    /// ngược lại <c>null</c> (⇒ dùng seed mặc định). Kết quả này đưa vào filter
    /// <c>c.CampaignId == null &amp;&amp; c.CandidateId == owner &amp;&amp; c.JobCategory == jc</c>.
    /// </summary>
    public static async Task<Guid?> ResolveOwnerAsync(
        InterviewDbContext db, Guid candidateId, JobCategory jobCategory, string language = "vi", CancellationToken ct = default)
    {
        var hasOwn = await db.RubricCriteria.AsNoTracking().AnyAsync(
            c => c.CampaignId == null
                 && c.CandidateId == candidateId
                 && c.JobCategory == jobCategory
                 && c.Language == language
                 && c.IsActive,
            ct);
        return hasOwn ? candidateId : (Guid?)null;
    }
}
