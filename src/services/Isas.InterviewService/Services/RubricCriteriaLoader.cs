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
/// <param name="CampaignRubricVersion">
/// B2B — phiên bản rubric mà BUỔI THI đã ghim (<c>practice_sessions.campaign_rubric_version</c>).
/// PHẢI nằm trong khoá cache: thiếu nó thì hai buổi cùng campaign nhưng ghim hai phiên bản khác nhau
/// dùng chung một entry cache ⇒ buổi này bị chấm bằng thước của buổi kia.
/// </param>
public readonly record struct RubricScopeKey(
    Guid? CampaignId,
    Guid? CandidateId,
    JobCategory? JobCategory,
    string Language = "vi",
    int? CampaignRubricVersion = null);

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
            ? new RubricScopeKey(campaignId, null, null,
                CampaignRubricVersion: session.CampaignRubricVersion)
            : new RubricScopeKey(null, session.CandidateId, session.JobCategory, session.Language);

    /// <summary>
    /// Nạp bộ tiêu chí của một phạm vi. E9: <c>.Include(Levels)</c> để có mức neo (câu mẫu là cột
    /// jsonb scalar trên chính level sau DB15) — tắt bằng <paramref name="includeLevels"/> ở call-site
    /// chỉ cần weight/maxScore để cộng điểm (không đổi TẬP dòng được chọn, chỉ đổi phần nạp kèm).
    /// </summary>
    public static async Task<List<RubricCriterion>> LoadAsync(
        InterviewDbContext db, RubricScopeKey key, CancellationToken ct = default,
        bool includeLevels = true)
    {
        var baseQuery = db.RubricCriteria.AsNoTracking();
        var query = includeLevels ? baseQuery.Include(c => c.Levels) : baseQuery;

        if (key.CampaignId is Guid campaignId)
        {
            query = query.Where(c => c.CampaignId == campaignId);

            // ⚠ `is_active` CỐ Ý KHÔNG có ở nhánh này khi buổi đã ghim phiên bản.
            //
            // Sau khi HR sửa mốc, bộ cũ bị HẠ CỜ is_active nhưng vẫn phải dùng được để chấm nốt những
            // buổi đã ghim nó. Thêm lại `is_active` vào đây là: buổi ghim v1 nạp về 0 tiêu chí ⇒
            // AnswerService bỏ qua publish ⇒ answer KHÔNG BAO GIỜ được chấm ⇒ session không đóng ⇒
            // ứng viên mất 1 credit mà không có kết quả (PAY-13). Không exception nào nổ.
            //
            // Nói cách khác `is_active` ở phạm vi B2B nay có nghĩa "bộ dùng cho buổi thi MỚI", KHÔNG
            // phải "bộ dùng để chấm". Phản trực giác — đừng "sửa cho đúng" bằng cách thêm lại.
            query = key.CampaignRubricVersion is int version
                ? query.Where(c => c.Version == version)
                : query.Where(c => c.IsActive);   // buổi có trước cột ghim (sau backfill: không nên tới)

            // OrderBy là chốt chặn rẻ cho `criteria[0].Version` ở AnswerService/republisher — nếu không
            // thì con dấu rubric_version của cả lượt chấm phụ thuộc thứ tự DB tình cờ trả về.
            return await query.OrderBy(c => c.Name).ToListAsync(ct);
        }

        // B2C — không có khái niệm phiên bản campaign, giữ nguyên luật cũ (rubric riêng BC16 tự
        // versioning bằng is_active trong RubricLibraryService).
        query = query.Where(c => c.IsActive);
        var jobCategory = key.JobCategory!.Value;
        var candidateId = key.CandidateId!.Value;
        var owner = await B2CRubricScope.ResolveOwnerAsync(db, candidateId, jobCategory, key.Language, ct);
        query = owner is Guid oid
            ? query.Where(c => c.CampaignId == null && c.CandidateId == oid
                               && c.JobCategory == jobCategory && c.Language == key.Language)
            : query.Where(c => c.CampaignId == null && c.CandidateId == null
                               && c.JobCategory == jobCategory && c.Language == key.Language);
        return await query.OrderBy(c => c.Name).ToListAsync(ct);
    }
}
