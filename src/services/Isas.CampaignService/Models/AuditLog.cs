namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Vết thao tác HR (D11) — ghi mọi mutation quan trọng để audit/đối chất.
    /// BK4: org_id = ORG sở hữu campaign; actor_user_id = cá nhân HR thao tác (giữ danh tính người, AUTH-8).
    /// </summary>
    public class AuditLog
    {
        public Guid Id { get; set; }
        public Guid? OrgId { get; set; }           // BK4: ORG sở hữu campaign (ownership context)
        public Guid ActorUserId { get; set; }      // cá nhân HR/AI thao tác (user sub — KHÔNG phải org)
        public AuditAction Action { get; set; }
        public string Entity { get; set; } = null!;   // "Campaign"
        public Guid EntityId { get; set; }
        public string? Summary { get; set; }
        public DateTime At { get; set; }
    }

    public enum AuditAction
    {
        CreateCampaign = 0,
        EditQuestions = 1,
        EditCriteria = 2,
        Publish = 3,
        Delete = 4,
        TransitionStatus = 5,
        Invite = 6,           // D1: mời ứng viên qua email (campaign_invitations)
        ScreenCandidates = 7, // C13: upload + sàng CV hàng loạt (campaign_candidates)
        EditCandidate = 8,    // C14: HR sửa email/fullName ứng viên sàng CV (campaign_candidates)
        ReissueInvitation = 9, // D4: phát lại lời mời — vô hiệu token cũ + phát token mới
        OverrideResult = 10,  // E11b: HR chốt/sửa điểm-kết-quả cuối của ứng viên (điểm AI = gợi ý)
        CreateApiKey = 11,    // F17: OrgAdmin cấp API key cho bên thứ ba (ATS)
        RevokeApiKey = 12     // F17: OrgAdmin thu hồi API key
    }
}
