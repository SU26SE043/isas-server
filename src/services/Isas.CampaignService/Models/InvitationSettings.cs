namespace Isas.CampaignService.Models
{
    // DB23 — cấu hình vòng đời token magic-link mời ứng viên.
    public class InvitationSettings
    {
        public const string SectionName = "Invitation";

        /// <summary>
        /// Hạn mặc định (ngày) cho token khi campaign KHÔNG có <c>expires_at</c>.
        /// Trước DB23 token của campaign không deadline có <c>expires_at = NULL</c> = **không bao giờ
        /// hết hạn** (magic-link sống vĩnh viễn). Nay luôn có hạn: campaign có deadline → dùng deadline
        /// (giữ ràng buộc token ≤ hạn campaign); không có → <c>created_at + DefaultExpiryDays</c>.
        /// </summary>
        public int DefaultExpiryDays { get; set; } = 14;
    }
}
