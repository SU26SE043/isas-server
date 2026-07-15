using System.Net;
using System.Net.Mail;

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

            var subject = $"Lời mời tham gia đánh giá — {campaignTitle}";
            var body = BuildHtmlBody(campaignTitle, joinLink, expiresAt);

            using var client = new SmtpClient(host, port)
            {
                Credentials = new NetworkCredential(from, password),
                EnableSsl = true
            };

            using var mailMessage = new MailMessage
            {
                From = new MailAddress(from),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            mailMessage.To.Add(toEmail);

            await client.SendMailAsync(mailMessage, ct);
        }

        private static string BuildHtmlBody(string campaignTitle, string joinLink, DateTime? expiresAt)
        {
            var expiryLine = expiresAt.HasValue
                ? $"<p>Lời mời có hiệu lực đến: <strong>{expiresAt.Value:yyyy-MM-dd HH:mm} UTC</strong>.</p>"
                : string.Empty;

            return $@"<p>Xin chào,</p>
<p>Bạn được mời tham gia đánh giá <strong>{campaignTitle}</strong>.</p>
<p>Nhấn vào liên kết dưới đây để tham gia:</p>
<p><a href=""{joinLink}"">{joinLink}</a></p>
{expiryLine}
<p>Trân trọng,<br/>ISAS</p>";
        }
    }
}
