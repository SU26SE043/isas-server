using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.Encodings.Web;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// SMTP sender cho email mời ứng viên — copy logic AuthService.EmailSender
    /// (SmtpClient + EnableSsl + NetworkCredential(EmailSettings:From, EmailSettings:Password)).
    /// Đọc <c>EmailSettings:Host/Port/From/Password</c>. Dựng HTML body kèm magic-link + hạn.
    /// </summary>
    public class CampaignEmailSender : ICampaignEmailSender
    {
        private readonly IConfiguration _config;

        public CampaignEmailSender(IConfiguration config)
        {
            _config = config;
        }

        public async Task SendInvitationEmailAsync(
            string toEmail,
            string campaignTitle,
            string joinLink,
            DateTime? expiresAt,
            DateTime? slotStartsAt,
            DateTime? slotEndsAt,
            DateTime? startsAt = null,
            string? orgName = null,
            bool faceVerifyEnabled = false,
            int? timeLimitMinutes = null,
            CancellationToken ct = default)
        {
            var host = _config["EmailSettings:Host"]
                ?? throw new InvalidOperationException("SMTP server not configured");
            var port = int.Parse(_config["EmailSettings:Port"]
                ?? throw new InvalidOperationException("SMTP port not configured"));
            var from = _config["EmailSettings:From"]
                ?? throw new InvalidOperationException("SMTP username not configured");
            var password = _config["EmailSettings:Password"]
                ?? throw new InvalidOperationException("SMTP password not configured");

            using var client = new SmtpClient(host, port)
            {
                Credentials = new NetworkCredential(from, password),
                EnableSsl = true
            };

            using var mailMessage = BuildMailMessage(
                from, toEmail, campaignTitle, joinLink, expiresAt, slotStartsAt, slotEndsAt,
                startsAt, orgName, faceVerifyEnabled, timeLimitMinutes);

            await client.SendMailAsync(mailMessage, ct);
        }

        /// <summary>
        /// Dựng <see cref="MailMessage"/> đa phần (plain text + HTML).
        /// <para>
        /// ⚠ KHÔNG dùng <c>MailMessage.Body</c> + <c>IsBodyHtml</c> ở đây: khi
        /// <c>AlternateViews</c> không rỗng, .NET dựng view từ <c>Body</c> mà BỎ QUA
        /// <c>IsBodyHtml</c> ⇒ bản HTML bị gửi dưới <c>Content-Type: text/plain</c>.
        /// </para>
        /// <para>
        /// Thứ tự có ý nghĩa: theo RFC 2046, part CUỐI CÙNG trong <c>multipart/alternative</c>
        /// là bản client ưu tiên hiển thị ⇒ plain text TRƯỚC, HTML SAU. Đảo lại thì ứng viên
        /// nhận email plain text và toàn bộ template HTML không bao giờ hiện ra.
        /// </para>
        /// Cả hai bất biến được khoá bằng <c>CampaignEmailSenderMimeTests</c> (đọc .eml thật).
        /// </summary>
        internal static MailMessage BuildMailMessage(
            string from,
            string toEmail,
            string campaignTitle,
            string joinLink,
            DateTime? expiresAt,
            DateTime? slotStartsAt,
            DateTime? slotEndsAt,
            DateTime? startsAt = null,
            string? orgName = null,
            bool faceVerifyEnabled = false,
            int? timeLimitMinutes = null)
        {
            var mailMessage = new MailMessage
            {
                From = new MailAddress(from),
                Subject = $"Lời mời tham gia đánh giá — {campaignTitle}"
            };
            mailMessage.To.Add(toEmail);

            mailMessage.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                BuildPlainTextBody(campaignTitle, joinLink, expiresAt, slotStartsAt, slotEndsAt,
                    startsAt, orgName, faceVerifyEnabled, timeLimitMinutes),
                Encoding.UTF8,
                MediaTypeNames.Text.Plain));
            mailMessage.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                BuildHtmlBody(campaignTitle, joinLink, expiresAt, slotStartsAt, slotEndsAt,
                    startsAt, orgName, faceVerifyEnabled, timeLimitMinutes),
                Encoding.UTF8,
                MediaTypeNames.Text.Html));

            return mailMessage;
        }

        /// <summary>
        /// Mốc hết hạn LUÔN in theo <see cref="CultureInfo.InvariantCulture"/>: trong chuỗi
        /// format tuỳ biến, <c>:</c> là time-separator phụ thuộc culture (máy <c>fi-FI</c> in
        /// <c>09.30</c>), nên bỏ qua sẽ khiến email đổi định dạng theo locale máy chủ —
        /// đúng lớp lỗi F16 (PDF <c>91,5</c> vs CSV <c>91.5</c>).
        /// </summary>
        private static string FormatExpiry(DateTime expiresAt) =>
            expiresAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        private static string FormatSlot(DateTime startsAt, DateTime endsAt)
        {
            var startsAtVn = VietnamTime.From(startsAt);
            var endsAtVn = VietnamTime.From(endsAt);
            return $"{startsAtVn:HH:mm}–{endsAtVn:HH:mm}, {startsAtVn:dd/MM/yyyy} (giờ VN)";
        }

        // CMP1-B4 — giờ chiến dịch MỞ (campaign.StartsAt), in theo giờ VN như FormatSlot (không phải
        // InvariantCulture như FormatExpiry: đây là mốc để NGƯỜI ĐỌC hình dung "mấy giờ tôi bấm vào
        // được", không phải mốc kỹ thuật UTC như hạn lời mời).
        private static string FormatOpensAt(DateTime startsAt)
        {
            var vn = VietnamTime.From(startsAt);
            return $"{vn:HH:mm} ngày {vn:dd/MM/yyyy} (giờ VN)";
        }

        /// <summary>
        /// CMP1-B4 — dòng chuẩn bị: "<c>{phút} phút</c>" và/hoặc "<c>cần camera và micro</c>", nối
        /// bằng " · " — CHỈ phần nào có dữ liệu mới xuất hiện. Dùng chung cho cả 2 bản thư nên hai bản
        /// không thể trôi lệch nhau (mẫu <c>ScoringCriteriaBuilder</c> — một nguồn cho hai nơi đọc).
        /// </summary>
        private static string? BuildPrepLine(bool faceVerifyEnabled, int? timeLimitMinutes)
        {
            var parts = new List<string>();
            if (timeLimitMinutes is int m) parts.Add($"{m} phút");
            if (faceVerifyEnabled) parts.Add("cần camera và micro");
            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }

        internal static string BuildHtmlBody(
            string campaignTitle, string joinLink, DateTime? expiresAt,
            DateTime? slotStartsAt, DateTime? slotEndsAt,
            DateTime? startsAt = null, string? orgName = null,
            bool faceVerifyEnabled = false, int? timeLimitMinutes = null)
        {
            var safeCampaignTitle = HtmlEncoder.Default.Encode(campaignTitle);
            var safeJoinLink = HtmlEncoder.Default.Encode(joinLink);
            var expiryCard = expiresAt.HasValue
                ? $@"<tr>
  <td style=""padding:16px 20px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:12px;color:#1e3a8a;font-size:14px;line-height:20px;"">
    <strong>Thời hạn tham gia</strong><br/>
    Vui lòng tham gia trước <strong>{FormatExpiry(expiresAt.Value)} UTC</strong>.
  </td>
</tr>
<tr><td style=""height:24px;font-size:1px;line-height:1px;"">&nbsp;</td></tr>"
                : string.Empty;
            var slotCard = slotStartsAt.HasValue && slotEndsAt.HasValue
                ? $@"<tr>
  <td style=""padding:16px 20px;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:12px;color:#14532d;font-size:14px;line-height:20px;"">
    <strong>Khung giờ phỏng vấn</strong><br/>
    {FormatSlot(slotStartsAt.Value, slotEndsAt.Value)}
  </td>
</tr>
<tr><td style=""height:24px;font-size:1px;line-height:1px;"">&nbsp;</td></tr>"
                : string.Empty;
            // CMP1-B4 — chỉ in khi startsAt còn Ở TƯƠNG LAI: quá khứ nghĩa là chiến dịch đã mở, nói
            // "mở từ" một mốc đã qua chỉ gây khó hiểu chứ không giúp ứng viên chuẩn bị gì thêm.
            var opensCard = startsAt.HasValue && startsAt.Value > DateTime.UtcNow
                ? $@"<tr>
  <td style=""padding:16px 20px;background:#fffbeb;border:1px solid #fde68a;border-radius:12px;color:#78350f;font-size:14px;line-height:20px;"">
    <strong>Phỏng vấn mở từ</strong><br/>
    {FormatOpensAt(startsAt.Value)}
  </td>
</tr>
<tr><td style=""height:24px;font-size:1px;line-height:1px;"">&nbsp;</td></tr>"
                : string.Empty;
            // KHÔNG HtmlEncoder ở đây (khác campaignTitle/joinLink/orgName): prepLine dựng HOÀN TOÀN
            // từ số nguyên + chuỗi hằng trong code, không có ký tự nào do người dùng nhập — encode
            // vào sẽ chỉ mã hoá tiếng Việt thành entity số vô ích (đúng cách FormatExpiry/FormatSlot
            // cũng KHÔNG encode vì cùng lý do: dữ liệu server tự tính, không phải input).
            var prepLine = BuildPrepLine(faceVerifyEnabled, timeLimitMinutes);
            var prepCard = prepLine is not null
                ? $@"<tr>
  <td style=""padding:16px 20px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:12px;color:#334155;font-size:14px;line-height:20px;"">
    <strong>Chuẩn bị trước khi vào phỏng vấn</strong><br/>
    {prepLine}
  </td>
</tr>
<tr><td style=""height:24px;font-size:1px;line-height:1px;"">&nbsp;</td></tr>"
                : string.Empty;

            var safeOrgName = string.IsNullOrWhiteSpace(orgName) ? null : HtmlEncoder.Default.Encode(orgName);
            var signatureHtml = safeOrgName is not null
                ? $"<strong>{safeOrgName}</strong><br/><span style=\"color:#94a3b8;\">Gửi qua nền tảng ISAS</span>"
                : "<strong>Đội ngũ ISAS</strong>";

            return $@"<!doctype html>
<html lang=""vi"">
<body style=""margin:0;padding:0;background:#f3f6fb;font-family:Arial,Helvetica,sans-serif;color:#172033;"">
  <table role=""presentation"" width=""100%"" cellspacing=""0"" cellpadding=""0"" border=""0"" style=""background:#f3f6fb;padding:32px 16px;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""100%"" cellspacing=""0"" cellpadding=""0"" border=""0"" style=""max-width:600px;background:#ffffff;border-radius:20px;overflow:hidden;"">
        <tr>
          <td bgcolor=""#132b5c"" style=""padding:28px 32px;background-color:#132b5c;background:linear-gradient(135deg,#132b5c,#2563eb);color:#ffffff;"">
            <div style=""font-size:13px;font-weight:700;letter-spacing:1.6px;"">ISAS · AI INTERVIEW</div>
            <div style=""margin-top:12px;font-size:28px;font-weight:700;line-height:36px;"">Bạn được mời phỏng vấn cùng AI</div>
          </td>
        </tr>
        <tr>
          <td style=""padding:32px;color:#172033;"">
            <p style=""margin:0 0 16px;color:#172033;font-size:16px;line-height:24px;"">Xin chào,</p>
            <p style=""margin:0 0 20px;color:#172033;font-size:16px;line-height:24px;"">Bạn đã được mời tham gia chiến dịch đánh giá:</p>
            <div style=""margin:0 0 24px;padding:18px 20px;background:#f8fafc;border-left:4px solid #2563eb;border-radius:8px;font-size:18px;font-weight:700;line-height:26px;"">{safeCampaignTitle}</div>
            <p style=""margin:0 0 24px;color:#172033;font-size:16px;line-height:24px;"">Nhấn nút bên dưới để xác nhận lời mời và bắt đầu hành trình phỏng vấn với AI.</p>
            <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"" style=""margin:0 0 24px;"">
              <tr><td align=""center"" bgcolor=""#2563eb"" style=""border-radius:10px;"">
                <a href=""{safeJoinLink}"" style=""display:inline-block;padding:14px 22px;color:#ffffff;font-size:16px;font-weight:700;line-height:20px;text-decoration:none;"">Tham gia phỏng vấn AI</a>
              </td></tr>
            </table>
            {slotCard}
            {opensCard}
            {prepCard}
            {expiryCard}
            <p style=""margin:0;font-size:13px;line-height:20px;color:#667085;"">Vì bảo mật, không chuyển tiếp email hoặc liên kết này cho người khác. Nếu nút không hoạt động, hãy mở liên kết sau trong trình duyệt:</p>
            <p style=""margin:8px 0 0;font-size:13px;line-height:20px;word-break:break-all;""><a href=""{safeJoinLink}"" style=""color:#2563eb;"">{safeJoinLink}</a></p>
          </td>
        </tr>
        <tr><td style=""padding:20px 32px;background:#f8fafc;color:#667085;font-size:13px;line-height:20px;"">Trân trọng,<br/>{signatureHtml}</td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";
        }

        internal static string BuildPlainTextBody(
            string campaignTitle, string joinLink, DateTime? expiresAt,
            DateTime? slotStartsAt, DateTime? slotEndsAt,
            DateTime? startsAt = null, string? orgName = null,
            bool faceVerifyEnabled = false, int? timeLimitMinutes = null)
        {
            var expiryLine = expiresAt.HasValue
                ? $"\nThời hạn tham gia: trước {FormatExpiry(expiresAt.Value)} UTC.\n"
                : string.Empty;
            var slotLine = slotStartsAt.HasValue && slotEndsAt.HasValue
                ? $"\nKhung giờ phỏng vấn: {FormatSlot(slotStartsAt.Value, slotEndsAt.Value)}.\n"
                : string.Empty;
            // CMP1-B4 — cùng điều kiện "còn ở tương lai" như bản HTML.
            var opensLine = startsAt.HasValue && startsAt.Value > DateTime.UtcNow
                ? $"\nPhỏng vấn mở từ: {FormatOpensAt(startsAt.Value)}.\n"
                : string.Empty;
            var prepLine = BuildPrepLine(faceVerifyEnabled, timeLimitMinutes);
            var prepLineText = prepLine is not null ? $"\nChuẩn bị trước khi vào phỏng vấn: {prepLine}.\n" : string.Empty;
            var signature = string.IsNullOrWhiteSpace(orgName)
                ? "Đội ngũ ISAS"
                : $"{orgName}\n(Gửi qua nền tảng ISAS)";

            return $"""
Xin chào,

Bạn được mời tham gia chiến dịch đánh giá: {campaignTitle}

Tham gia phỏng vấn AI tại:
{joinLink}
{slotLine}
{opensLine}
{prepLineText}
{expiryLine}
Vì bảo mật, không chuyển tiếp email hoặc liên kết này cho người khác.

Trân trọng,
{signature}
""";
        }
    }
}
