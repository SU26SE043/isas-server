namespace Isas.CampaignService.DTOs
{
    /// <summary>D1 — Distribution đường 1: mời thẳng qua danh sách email.</summary>
    public class CreateInvitationsRequest
    {
        public List<string> Emails { get; set; } = new();
    }

    public class InvitationItem
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = null!;
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>Email hỏng/trùng/đã mời → nằm ở đây, KHÔNG chặn cả batch.</summary>
    public class FailedInvitationItem
    {
        public string Email { get; set; } = null!;
        public string Reason { get; set; } = null!;
    }

    public class CreateInvitationsResponse
    {
        public List<InvitationItem> Created { get; set; } = new();
        public List<FailedInvitationItem> Failed { get; set; } = new();
    }
}
