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

    /// <summary>402 — BK14: ví credit của tổ chức không đủ để reserve khi ứng viên bắt đầu phỏng vấn (PAY-5).</summary>
    public class InsufficientOrgCreditException : Exception
    {
        public InsufficientOrgCreditException(string message) : base(message) { }
    }

    /// <summary>Campaign đã dùng hết số phiên đang chạy được cấu hình.</summary>
    public class CampaignInterviewCapacityExceededException : Exception
    {
        public CampaignInterviewCapacityExceededException(string message) : base(message) { }
    }
}
