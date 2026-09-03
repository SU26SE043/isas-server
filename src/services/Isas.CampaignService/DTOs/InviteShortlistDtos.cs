namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// C15 — Distribution đường 2 (từ shortlist sàng CV): HR chọn top sau ranking → mời hàng loạt.
    /// Khác D1 (đường 1, mời thẳng theo email): ở đây mời theo <c>candidateIds</c>, email TÁCH TỪ CV
    /// (<c>campaign_candidates.email</c>, parse sẵn ở C13). Invitation gắn <c>campaign_candidate_id</c>.
    /// </summary>
    public class InviteShortlistRequest
    {
        public List<Guid> CandidateIds { get; set; } = new();

        /// <summary>
        /// RNK1 · HĐ-6 — mặc định (false) BỎ QUA ứng viên không đủ điều kiện loại (thiếu bằng chứng
        /// cho nhu cầu <c>isMustHave</c>) ⇒ vào <c>failed[]</c>. true = HR chủ động mời cả nhóm đó.
        /// </summary>
        public bool IncludeIneligible { get; set; }
    }

    /// <summary>1 ứng viên đã mời thành công (Analyzed → Invited).</summary>
    public class InvitedCandidateItem
    {
        public Guid CandidateId { get; set; }
        public Guid InvitationId { get; set; }
        public string Email { get; set; } = null!;
    }

    /// <summary>Ứng viên KHÔNG mời được (thiếu email / sai trạng thái / đã mời) — không chặn item khác.</summary>
    public class FailedInviteItem
    {
        public Guid CandidateId { get; set; }
        public string Reason { get; set; } = null!;
    }

    public class InviteShortlistResponse
    {
        public List<InvitedCandidateItem> Invited { get; set; } = new();
        public List<FailedInviteItem> Failed { get; set; } = new();
    }
}
