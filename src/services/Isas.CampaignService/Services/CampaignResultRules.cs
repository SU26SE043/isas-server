using Isas.CampaignService.DTOs;
using Isas.Shared.Scoring;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// MỘT NGUỒN SỰ THẬT cho ba luật đọc (read-time) mà nhiều màn hình cùng hiển thị: kết luận Pass/Fail
    /// của một dòng ranking (E5/E11b/RNK1·HĐ-5/CAMP-11), trạng thái giao lời mời (GET /invitations), và
    /// phép ghép "lời mời này đã join chưa" (FX1). <c>GetCampaignResultsAsync</c>/<c>GetInvitationsAsync</c>
    /// (bảng chi tiết) và <c>CampaignAnalyticsService</c> (tổng hợp theo org) ĐỀU gọi vào đây — hai chỗ tự
    /// chép công thức thì tổng ở dashboard và kết luận trên bảng lệch nhau mà không test nào đỏ.
    /// </summary>
    internal static class CampaignResultRules
    {
        /// <summary>
        /// Kết luận Pass/Fail cho một dòng ranking. Thứ tự ưu tiên có chủ ý:
        /// HR override thắng tất cả (E11b) → rớt SÀN tiêu chí nào ⇒ Fail (RNK1·HĐ-5) → so ngưỡng
        /// Employer <c>pass_score_pct</c> (CAMP-11) → ngưỡng null ⇒ <c>null</c> (HR quyết tay).
        /// </summary>
        public static string? ResolveResult(
            string? overrideResult, int belowCutoffCount, int? passScorePct, decimal effectiveScore)
            => overrideResult
                ?? (belowCutoffCount > 0 ? "Fail"
                    : passScorePct is null ? null
                    : effectiveScore >= passScorePct.Value ? "Pass" : "Fail");

        /// <summary>
        /// Trạng thái giao lời mời, suy read-time. Thứ tự ưu tiên có chủ ý (xem
        /// <see cref="InvitationDeliveryStatus"/>): Revoked đứng TRƯỚC Joined để lời mời cũ sau reissue (D4)
        /// không hiện Joined nhờ lời mời MỚI cùng email. <c>Sent</c> = SMTP đã gửi thật (<c>email_sent_at</c>),
        /// KHÔNG phải <c>created_at</c>/<c>sent_at</c> (mới vào outbox = Queued).
        /// </summary>
        public static string ResolveDeliveryStatus(
            DateTime? revokedAt, bool joined, DateTime expiresAt, DateTime? emailSentAt, DateTime now)
        {
            if (revokedAt is not null) return InvitationDeliveryStatus.Revoked;
            if (joined) return InvitationDeliveryStatus.Joined;
            if (expiresAt <= now) return InvitationDeliveryStatus.Expired;
            if (emailSentAt is not null) return InvitationDeliveryStatus.Sent;
            return InvitationDeliveryStatus.Queued;
        }

        /// <summary>Tiêu chí campaign rút gọn đúng ba cột mà điểm sàn cần (khớp id / khớp tên / sàn).</summary>
        public readonly record struct CutoffCriterion(Guid Id, string Name, int? MinPct);

        // RNK1 · HĐ-5 — điểm sàn theo tiêu chí, READ-TIME. Với mỗi tiêu chí trong bó biến (snapshot),
        // khớp về campaign_criteria: có `criterionId` ⇒ khớp theo id (matchedBy "id"); snapshot GHI
        // TRƯỚC RNK1 không có id ⇒ khớp theo TÊN (Trim / OrdinalIgnoreCase, matchedBy "name"). Tiêu chí
        // khớp có `min_pct` và `pct < min_pct` ⇒ dòng đó rớt sàn.
        //
        // KHÔNG ghim sàn vào snapshot: `min_pct` phải đổi được lúc chạy (như pass_score_pct), nên phải
        // đọc từ campaign_criteria HIỆN TẠI, không phải giá trị lúc buổi thi đóng.
        public static List<BelowCutoffItem> ComputeBelowCutoff(
            ScoringInputsSnapshot? snapshot, IEnumerable<CutoffCriterion> criteria)
        {
            var result = new List<BelowCutoffItem>();
            if (snapshot?.Criteria is not { Count: > 0 } snapCriteria) return result;

            var byId = new Dictionary<Guid, CutoffCriterion>();
            var byName = new Dictionary<string, CutoffCriterion>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in criteria)
            {
                byId[c.Id] = c;
                byName.TryAdd(c.Name.Trim(), c);
            }

            foreach (var sc in snapCriteria)
            {
                CutoffCriterion match;
                string matchedBy;
                if (sc.CriterionId is Guid cid && byId.TryGetValue(cid, out var byIdMatch))
                {
                    match = byIdMatch;
                    matchedBy = "id";
                }
                else if (byName.TryGetValue((sc.Name ?? string.Empty).Trim(), out var byNameMatch))
                {
                    match = byNameMatch;
                    matchedBy = "name";
                }
                else continue;

                if (match.MinPct is int minPct && sc.Pct < minPct)
                    result.Add(new BelowCutoffItem
                    {
                        CriterionId = match.Id,
                        Name = match.Name,
                        Pct = sc.Pct,
                        MinPct = minPct,
                        MatchedBy = matchedBy,
                    });
            }
            return result;
        }

        /// <summary>Một dòng <c>campaign_membership</c> rút gọn đúng bốn cột mà phép ghép lời mời↔join cần.</summary>
        public readonly record struct MembershipJoinRow(Guid? InvitationId, Guid? CvSubmissionId, string? Email, DateTime? JoinedAt);

        /// <summary>
        /// Chỉ mục "lời mời nào đã join" dựng từ membership của MỘT campaign. FX1 — ghép CHÍNH XÁC theo
        /// quan hệ <c>membership.invitation_id</c> trước. Hai nhánh cũ (<c>cv_submission_id</c> rồi email) chỉ
        /// còn là FALLBACK cho membership LỊCH SỬ chưa có link (join trước FX1, và migration cố ý không
        /// backfill khi không chắc). Membership ĐÃ có link thì KHÔNG được ghép bằng email nữa — nếu không,
        /// lời mời thứ hai cùng email vẫn "thơm lây" trạng thái Joined của lời mời thứ nhất, tức là đúng
        /// cái suy đoán mà quan hệ này sinh ra để bỏ. Email so case-insensitive vì đường-1 chỉ Trim()
        /// còn đường-2 đã lowercase từ C13.
        /// </summary>
        public sealed class InvitationJoinIndex
        {
            private readonly Dictionary<Guid, DateTime?> _byInvitation;
            private readonly Dictionary<Guid, DateTime?> _byCv;
            private readonly Dictionary<string, DateTime?> _byEmail;

            public InvitationJoinIndex(IEnumerable<MembershipJoinRow> memberships)
            {
                var rows = memberships as IReadOnlyCollection<MembershipJoinRow> ?? memberships.ToList();

                _byInvitation = rows
                    .Where(m => m.InvitationId is not null)
                    .GroupBy(m => m.InvitationId!.Value)
                    .ToDictionary(g => g.Key, g => g.Max(m => m.JoinedAt));

                var legacy = rows.Where(m => m.InvitationId is null).ToList();
                _byCv = legacy
                    .Where(m => m.CvSubmissionId is not null)
                    .GroupBy(m => m.CvSubmissionId!.Value)
                    .ToDictionary(g => g.Key, g => g.Max(m => m.JoinedAt));
                _byEmail = legacy
                    .Where(m => !string.IsNullOrWhiteSpace(m.Email))
                    .GroupBy(m => m.Email!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Max(m => m.JoinedAt), StringComparer.OrdinalIgnoreCase);
            }

            /// <summary>Lời mời đã join chưa; nếu có thì <paramref name="joinedAt"/> = mốc join (có thể null với dữ liệu cũ).</summary>
            public bool TryFind(Guid invitationId, Guid? campaignCandidateId, string email, out DateTime? joinedAt)
            {
                if (_byInvitation.TryGetValue(invitationId, out joinedAt)) return true;
                if (campaignCandidateId is Guid ccid && _byCv.TryGetValue(ccid, out joinedAt)) return true;
                if (_byEmail.TryGetValue(email.Trim(), out joinedAt)) return true;
                joinedAt = null;
                return false;
            }
        }
    }
}
