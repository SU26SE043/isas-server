using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// "Chủ" bộ tiêu chí chấm của một answer/session.
///
/// B2B = campaign (mọi ứng viên trong campaign dùng chung tiêu chí → KHÔNG kèm candidate vào key,
/// nếu không B2B lại tách nhóm theo từng ứng viên = quay về N+1 mà DB29 vừa gỡ);
/// B2C = (candidate, nghề, ngôn ngữ) theo BC16 + Q8.
/// </summary>
public readonly record struct RubricScopeKey(
    Guid? CampaignId,
    Guid? CandidateId,
    JobCategory? JobCategory,
    string Language = "vi");

/// <summary>
/// NGUỒN DUY NHẤT nạp tiêu chí chấm (E1/BC16/E9).
///
/// Trước đây cùng một câu truy vấn bị chép làm BA bản — <c>AnswerService.LoadActiveCriteriaAsync</c>
/// (lúc publish job), một bản INLINE trong <c>AnswerService.SaveResultAsync</c> (guard E8/E9 lúc
/// callback), và <c>StuckAnswerRepublisher.LoadCriteriaAsync</c> (đường cứu answer kẹt). Ba bản đó
/// PHẢI chọn cùng một bộ tiêu chí, vì:
///   • publish ↔ callback lệch nhau ⇒ mọi criterionId vừa gửi đi chấm bị guard E8 coi là "criterion
///     lạ" và BỎ ⇒ answer mất sạch điểm, KHÔNG exception nào nổ;
///   • đường upload ↔ đường republisher lệch nhau ⇒ cùng một answer sinh hai <c>rubric_version</c>
///     khác nhau ⇒ <c>attemptsForVersion</c> không bao giờ đủ N ⇒ answer kẹt <c>Scoring</c> vĩnh viễn.
/// Repo đã học đúng bài này một lần và đẻ ra <see cref="ScoringCriteriaBuilder"/> để hai đường
/// <em>build</em> không lệch — nhưng phần <em>load</em> vẫn còn ba bản. Đây là chỗ gộp lại.
/// </summary>
public static class RubricCriteriaLoader
{
    /// <summary>Khoá phạm vi rubric suy từ session (dùng cho đường publish + callback).</summary>
    public static RubricScopeKey KeyFor(PracticeSession session)
        => session.CampaignId is Guid campaignId
            ? new RubricScopeKey(campaignId, null, null)
            : new RubricScopeKey(null, session.CandidateId, session.JobCategory, session.Language);

    /// <summary>
    /// Nạp bộ tiêu chí của một phạm vi. E9: <c>.Include(Levels)</c> để có mức neo (câu mẫu là cột
    /// jsonb scalar trên chính level sau DB15).
    /// </summary>
    public static async Task<List<RubricCriterion>> LoadAsync(
        InterviewDbContext db, RubricScopeKey key, CancellationToken ct = default)
    {
        var query = db.RubricCriteria.AsNoTracking()
            .Include(c => c.Levels)
            .Where(c => c.IsActive);

        if (key.CampaignId is Guid campaignId)
            return await query.Where(c => c.CampaignId == campaignId).ToListAsync(ct);

        // BC16: B2C ưu tiên rubric RIÊNG của candidate cho (nghề, ngôn ngữ), else seed mặc định.
        var jobCategory = key.JobCategory!.Value;
        var candidateId = key.CandidateId!.Value;
        var owner = await B2CRubricScope.ResolveOwnerAsync(db, candidateId, jobCategory, key.Language, ct);
        query = owner is Guid oid
            ? query.Where(c => c.CampaignId == null && c.CandidateId == oid
                               && c.JobCategory == jobCategory && c.Language == key.Language)
            : query.Where(c => c.CampaignId == null && c.CandidateId == null
                               && c.JobCategory == jobCategory && c.Language == key.Language);
        return await query.ToListAsync(ct);
    }
}
