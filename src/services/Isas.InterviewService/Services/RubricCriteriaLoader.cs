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
/// <param name="B2COwnerId">
/// B2C — CHỦ bộ tiêu chí đã ghim: <c>null</c> = bộ chuẩn hệ thống, <c>!= null</c> = rubric riêng của
/// ứng viên (BC16). Chỉ có nghĩa khi <paramref name="B2CRubricVersion"/> khác null.
/// <para>⚠ Không suy được từ <paramref name="CandidateId"/>: <c>CandidateId</c> luôn là chủ BUỔI THI,
/// còn cột này là chủ BỘ TIÊU CHÍ — hai thứ khác nhau đúng ở ca buổi dùng bộ chuẩn.</para>
/// </param>
/// <param name="B2CRubricVersion">
/// B2C — phiên bản đã ghim TRONG phạm vi <paramref name="B2COwnerId"/>. Cùng lý do phải nằm trong
/// khoá cache như <paramref name="CampaignRubricVersion"/>: hai buổi cùng (ứng viên, nghề, ngôn ngữ)
/// nhưng ghim hai phiên bản khác nhau KHÔNG được dùng chung entry cache của republisher.
/// </param>
public readonly record struct RubricScopeKey(
    Guid? CampaignId,
    Guid? CandidateId,
    JobCategory? JobCategory,
    string Language = "vi",
    int? CampaignRubricVersion = null,
    Guid? B2COwnerId = null,
    int? B2CRubricVersion = null);

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
            : new RubricScopeKey(null, session.CandidateId, session.JobCategory, session.Language,
                B2COwnerId: session.B2CRubricOwnerId,
                B2CRubricVersion: session.B2CRubricVersion);

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

        // ─── B2C ────────────────────────────────────────────────────────────────────────────────
        var jobCategory = key.JobCategory!.Value;
        query = query.Where(c => c.CampaignId == null
                                 && c.JobCategory == jobCategory && c.Language == key.Language);

        if (key.B2CRubricVersion is int pinnedVersion)
        {
            // ⚠ `is_active` CỐ Ý KHÔNG có ở nhánh này — ĐỐI XỨNG nhánh B2B ở trên, và vì đúng một lý do.
            //
            // Admin sửa bộ chuẩn (hoặc ứng viên sửa rubric riêng) làm bộ cũ bị HẠ CỜ is_active, nhưng bộ
            // đó vẫn phải dùng được để chấm nốt những buổi đã ghim nó. Thêm lại `is_active` vào đây là:
            // buổi ghim v1 nạp về 0 tiêu chí ⇒ AnswerService bỏ qua publish ⇒ answer KHÔNG BAO GIỜ được
            // chấm ⇒ session không đóng ⇒ ứng viên mất 1 credit mà không có kết quả (PAY-13). Không
            // exception nào nổ, không log nào đỏ.
            //
            // `is_active` ở đây cũng mang nghĩa "bộ dùng cho buổi thi MỚI", KHÔNG phải "bộ dùng để chấm".
            // Phản trực giác — đừng "sửa cho đúng" bằng cách thêm lại.
            //
            // Chủ bộ tiêu chí lấy từ CON DẤU của buổi, KHÔNG hỏi lại `ResolveOwnerAsync`: hỏi lại là
            // hỏi trạng thái HIỆN TẠI, mà ứng viên bấm "Lưu rubric riêng" giữa buổi sẽ đổi câu trả lời
            // đó ⇒ publish-time và callback-time chọn hai bộ khác nhau ⇒ E8 bỏ mọi criterionId (mất
            // sạch điểm, im lặng). Tách hai nhánh thay vì so thẳng với biến nullable: `c.CandidateId ==
            // <biến null>` không phải lúc nào cũng dịch thành `IS NULL`.
            query = key.B2COwnerId is Guid pinnedOwner
                ? query.Where(c => c.CandidateId == pinnedOwner)
                : query.Where(c => c.CandidateId == null);
            return await query.Where(c => c.Version == pinnedVersion)
                .OrderBy(c => c.Name).ToListAsync(ct);
        }

        // Buổi có TRƯỚC cặp cột ghim (và migration không suy lại được thước đo đã dùng) → giữ nguyên
        // luật cũ: bộ đang hiệu lực, ưu tiên rubric riêng else bộ chuẩn.
        query = query.Where(c => c.IsActive);
        var candidateId = key.CandidateId!.Value;
        var owner = await B2CRubricScope.ResolveOwnerAsync(db, candidateId, jobCategory, key.Language, ct);
        query = owner is Guid oid
            ? query.Where(c => c.CandidateId == oid)
            : query.Where(c => c.CandidateId == null);
        return await query.OrderBy(c => c.Name).ToListAsync(ct);
    }
}
