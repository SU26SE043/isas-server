using System.Globalization;
using System.Net.Mail;
using System.Text;
using Isas.CampaignService.Services;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Khoá tầng MIME của email mời — tầng mà <c>CampaignEmailSenderTemplateTests</c> (chỉ so chuỗi)
/// và <c>InvitationEmailConsumerTests</c> (mock <c>ICampaignEmailSender</c>) đều KHÔNG chạm tới.
/// Chính khoảng trống đó từng để lọt bug: bản HTML bị gửi dưới <c>Content-Type: text/plain</c>
/// và đứng TRƯỚC bản plain ⇒ theo RFC 2046 client hiển thị plain text, template HTML không bao giờ hiện.
/// Test ghi <see cref="MailMessage"/> thật ra file .eml rồi đọc lại header — không suy luận.
/// </summary>
public class CampaignEmailSenderMimeTests
{
    private static readonly DateTime Expires = new(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc);

    private static string WriteEml()
    {
        var pickup = Path.Combine(Path.GetTempPath(), "isas-mime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pickup);
        try
        {
            using var client = new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = pickup
            };
            using var message = CampaignEmailSender.BuildMailMessage(
                "noreply@isas.test", "candidate@isas.test", "Backend Q3", "https://fe.test/invite/tok", Expires, null, null);
            client.Send(message);
            return File.ReadAllText(Directory.GetFiles(pickup, "*.eml").Single());
        }
        finally
        {
            Directory.Delete(pickup, recursive: true);
        }
    }

    private static List<(string ContentType, string Body)> ParseParts(string eml)
    {
        const string marker = "boundary=";
        var boundary = eml[(eml.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]
            .Split('\n')[0].Trim().Trim('"');

        var parts = new List<(string, string)>();
        foreach (var chunk in eml.Split("--" + boundary))
        {
            var trimmed = chunk.Trim();
            if (trimmed.Length == 0 || trimmed == "--") continue;

            var split = trimmed.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0) split = trimmed.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0) continue;

            var headers = trimmed[..split];
            var rawBody = trimmed[split..].Trim();
            var contentType = headers.Split('\n').Select(l => l.Trim()).FirstOrDefault(
                l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            var body = headers.Contains("base64", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(rawBody.Replace("\r", "").Replace("\n", "")))
                : rawBody;

            parts.Add((contentType, body));
        }
        return parts;
    }

    [Fact]
    public void Mime_ChinhXacHaiPart_PlainTruoc_HtmlSau()
    {
        var eml = WriteEml();
        Assert.Contains("multipart/alternative", eml);

        var parts = ParseParts(eml);
        Assert.Equal(2, parts.Count);

        // Bản plain phải đứng TRƯỚC…
        Assert.Contains("text/plain", parts[0].ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("text/html", parts[0].ContentType, StringComparison.OrdinalIgnoreCase);

        // …và bản HTML là part CUỐI: RFC 2046 nói đó mới là bản client ưu tiên hiển thị.
        Assert.Contains("text/html", parts[^1].ContentType, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mime_NoiDungNamDungPart_KhongHoanDoi()
    {
        var parts = ParseParts(WriteEml());

        var plain = parts.Single(p => p.ContentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)).Body;
        var html = parts.Single(p => p.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)).Body;

        Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tham gia phỏng vấn AI", html);

        // Bản plain là plain thật, không phải HTML bị dán nhãn nhầm.
        Assert.DoesNotContain("<!doctype html>", plain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<td", plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://fe.test/invite/tok", plain);
    }

    [Fact]
    public void Mime_HeaderNenCoMauDuPhong_ChoOutlookBoQuaGradient()
    {
        var parts = ParseParts(WriteEml());
        var html = parts.Single(p => p.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)).Body;

        // Outlook (engine Word) bỏ qua linear-gradient. Thiếu màu nền dự phòng thì
        // chữ #ffffff nằm trên nền trắng ⇒ tiêu đề vô hình.
        var header = html.Split('\n').Single(l => l.Contains("linear-gradient", StringComparison.Ordinal));
        Assert.Contains("background-color:#132b5c", header, StringComparison.Ordinal);
        Assert.Contains("bgcolor=\"#132b5c\"", header, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HanChot_KhongDoiDinhDangTheoLocaleMayChu(bool html)
    {
        // Culture tuỳ biến thay vì tên culture có thật: không phụ thuộc ICU của máy chạy test.
        var exotic = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        exotic.DateTimeFormat.TimeSeparator = "•";
        exotic.DateTimeFormat.DateSeparator = "•";

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = exotic;
            var body = html
                ? CampaignEmailSender.BuildHtmlBody("Backend Q3", "https://fe.test/invite/tok", Expires, null, null)
                : CampaignEmailSender.BuildPlainTextBody("Backend Q3", "https://fe.test/invite/tok", Expires, null, null);

            Assert.Contains("2026-08-15 09:30 UTC", body);
            Assert.DoesNotContain("•", body);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
