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

    /// <summary>
    /// CAMP-20 — quản trị viên CHƯA soạn bộ chuẩn B2C cho tổ hợp (nghề, ngôn ngữ) được hỏi
    /// (Interview trả 404). Đây là "chưa có", KHÔNG phải "hệ thống hỏng".
    ///
    /// <para><b>KẾ THỪA <see cref="DownstreamServiceException"/> là điểm mấu chốt, không phải tiện
    /// tay:</b> hai đường tiêu thụ cần hai mã HTTP khác nhau cho cùng một sự kiện —</para>
    /// <list type="bullet">
    /// <item><b>xem trước</b> (<c>GET .../system-default/preview</c>) là câu hỏi <i>"có sẵn không?"</i>
    /// ⇒ <b>404</b>, một câu trả lời bình thường;</item>
    /// <item><b>chép</b> (<c>POST .../from-system-default</c>) là mệnh lệnh <i>"chép hộ tôi"</i> mà
    /// hệ thống không thực hiện được ⇒ giữ nguyên <b>502</b> như hợp đồng đã chốt với FE.</item>
    /// </list>
    ///
    /// <para>Nhờ kế thừa, khối <c>catch (DownstreamServiceException)</c> của đường chép bắt được loại
    /// này y như trước ⇒ thêm ngoại lệ mới KHÔNG đổi một hành vi nào đang có. Đường xem trước bắt
    /// loại DẪN XUẤT trước, nên nó là chỗ DUY NHẤT phân biệt.</para>
    /// </summary>
    public class SystemRubricNotFoundException : DownstreamServiceException
    {
        public SystemRubricNotFoundException(string message) : base(message) { }
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

    /// <summary>409 — ứng viên bắt đầu mới ngoài khoảng thời gian slot đã được phân.</summary>
    public class OutsideSlotWindowException : Exception
    {
        public DateTime StartsAt { get; }
        public DateTime EndsAt { get; }

        public OutsideSlotWindowException(DateTime startsAt, DateTime endsAt, string message) : base(message)
        {
            StartsAt = startsAt;
            EndsAt = endsAt;
        }
    }
}
