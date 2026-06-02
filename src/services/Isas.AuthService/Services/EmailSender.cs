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
        //public async Task<string> GenerateAndStoreOtpAsync(string email)
        //{
        //    var otp = new Random().Next(100000, 999999).ToString();
        //    var key = GetCacheKey(email);

        //    var options = new DistributedCacheEntryOptions
        //    {
        //        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OTP_EXPIRY_MINUTES)
        //    };

        //    await _cache.SetStringAsync(key, otp, options);
        //    return otp;
        //}

        //public async Task<bool> ValidateOtpAsync(string email, string otp)
        //{
        //    var key = GetCacheKey(email);
        //    var stored = await _cache.GetStringAsync(key);
        //    return stored != null && stored == otp;
        //}

        //public async Task InvalidateOtpAsync(string email)
        //{
        //    await _cache.RemoveAsync(GetCacheKey(email));
        //}
        //private static string GetCacheKey(string email) => $"otp:forgot-password:{email.ToLower()}";

        //public async Task SendOtpEmailAsync(string toEmail, string otp)
        //{
        //    var smtpHost = _configuration["Email:SmtpHost"];
        //    var smtpPort = int.Parse(_configuration["Email:SmtpPort"]!);
        //    var username = _configuration["Email:Username"];
        //    var password = _configuration["Email:Password"];
        //    var fromEmail = _configuration["Email:From"];
        //    var fromName = _configuration["Email:FromName"] ?? "ISAS Support";

        //    using var client = new SmtpClient(smtpHost, smtpPort)
        //    {
        //        Credentials = new NetworkCredential(username, password),
        //        EnableSsl = true
        //    };

        //    var mail = new MailMessage
        //    {
        //        From = new MailAddress(fromEmail!, fromName),
        //        Subject = "Your Password Reset Code",
        //        Body = BuildEmailBody(otp),
        //        IsBodyHtml = true
        //    };
        //    mail.To.Add(toEmail);

        //    await client.SendMailAsync(mail);
        //}

        //private static string BuildEmailBody(string otp) => 
        //    $"""
        //    <div style="font-family:Arial,sans-serif;max-width:480px;margin:auto;padding:32px;
        //                border:1px solid #e5e7eb;border-radius:8px">
        //      <h2 style="color:#1d4ed8;margin-bottom:8px">Password Reset Request</h2>
        //      <p style="color:#374151">
        //        Use the code below to reset your password.
        //        It expires in <strong>10 minutes</strong>.
        //      </p>
        //      <div style="background:#f3f4f6;border-radius:8px;padding:24px;
        //                  text-align:center;margin:24px 0">
        //        <span style="font-size:40px;font-weight:bold;letter-spacing:12px;
        //                     color:#1d4ed8">{otp}</span>
        //      </div>
        //      <p style="color:#6b7280;font-size:13px">
        //        If you didn't request this, you can safely ignore this email.
        //      </p>
        //    </div>
        //    """;
    }
}
