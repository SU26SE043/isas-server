namespace Isas.CampaignService.Services
{
    /// <summary>410 Gone — magic-link không còn dùng được (đã revoke / hết hạn / campaign đã đóng).</summary>
    public class InvitationGoneException : Exception
    {
        public InvitationGoneException(string message) : base(message) { }
    }

    /// <summary>502 — service phụ thuộc (Auth provision / Interview session) không phản hồi.</summary>
    public class DownstreamServiceException : Exception
    {
        public DownstreamServiceException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
