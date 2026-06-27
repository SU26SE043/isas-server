namespace Isas.CampaignService.Models
{
    /// <summary>
    /// Vết thao tác HR (D11) — ghi mọi mutation quan trọng để audit/đối chất.
    /// org_id nullable vì Organization chưa build (dùng actor_user_id = employer_id).
    /// </summary>
    public class AuditLog
    {
        public Guid Id { get; set; }
        public Guid? OrgId { get; set; }
        public Guid ActorUserId { get; set; }      // employer_id (chờ org → đổi sang org member)
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
        TransitionStatus = 5
    }
}
