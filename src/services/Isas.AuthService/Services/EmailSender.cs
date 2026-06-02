using Microsoft.AspNetCore.Identity.UI.Services;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace Isas.AuthService.Services
{
    public class EmailSender : IEmailSender
    {
        private readonly IConfiguration _config;

        public EmailSender(IConfiguration config)
        {
            _config = config;
        }

        public async Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            var _smtpServer = _config["EmailSettings:Host"] ?? throw new InvalidOperationException("SMTP server not configured");
            var _smtpPort = int.Parse(_config["EmailSettings:Port"] ?? throw new InvalidOperationException("SMTP port not configured"));
            var _smtpUser = _config["EmailSettings:From"] ?? throw new InvalidOperationException("SMTP username not configured");
            var _smtpPass = _config["EmailSettings:Password"] ?? throw new InvalidOperationException("SMTP password not configured");

            using (var client = new SmtpClient(_smtpServer, _smtpPort))
            {
                client.Credentials = new NetworkCredential(_smtpUser, _smtpPass);
                client.EnableSsl = true;

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(_smtpUser),
                    Subject = subject,
                    Body = htmlMessage,
                    IsBodyHtml = true
                };
                mailMessage.To.Add(email);

                await client.SendMailAsync(mailMessage);
            }
        }
    }
}
