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

    /// <param name="expiresAt">null ⇒ dùng <see cref="Expires"/> (giữ hành vi 4 test cũ nguyên vẹn).</param>
    /// <param name="startsAt">CMP1-B4 — giờ campaign MỞ, khác hẳn <paramref name="expiresAt"/> (hạn lời
    /// mời). Đây đúng là khe nối <c>BuildMailMessage</c> từng KHÔNG có test nào chạm tới: 4 tham số này
    /// (+ orgName/faceVerifyEnabled/timeLimitMinutes) từng chỉ được gọi bằng chữ ký CŨ 7 tham số ở đây.</param>
    private static string WriteEml(
        DateTime? expiresAt = null,
        DateTime? startsAt = null,
        string? orgName = null,
        bool faceVerifyEnabled = false,
        int? timeLimitMinutes = null)
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
                "noreply@isas.test", "candidate@isas.test", "Backend Q3", "https://fe.test/invite/tok",
                expiresAt ?? Expires, null, null,
                startsAt, orgName, faceVerifyEnabled, timeLimitMinutes);
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

    /// <summary>
    /// KHE NỐI <c>BuildMailMessage</c> — trước bản này, <c>BuildPlainTextBody</c>/<c>BuildHtmlBody</c>
    /// đều được test GỌI THẲNG (không qua <c>BuildMailMessage</c>), nên không test nào chứng minh
    /// <c>BuildMailMessage</c> truyền ĐÚNG biến vào ĐÚNG tham số. Mutation đã đo: đổi
    /// <c>startsAt</c> thành <c>expiresAt</c> ở lời gọi <c>BuildPlainTextBody</c> bên trong
    /// <c>BuildMailMessage</c> — 1162/1162 test cũ vẫn XANH.
    ///
    /// <para>Test dùng HAI mốc thời gian khác định dạng hiển thị RÕ RỆT (startsAt →
    /// <c>dd/MM/yyyy</c> giờ VN qua <c>FormatOpensAt</c>; expiresAt → <c>yyyy-MM-dd HH:mm UTC</c>
    /// qua <c>FormatExpiry</c>) để một lần hoán đổi tham số lộ ra ngay: nếu <c>startsAt</c> bị thay
    /// bằng <c>expiresAt</c> thì dòng "Phỏng vấn mở từ" sẽ mất mốc <c>10/09/2026</c> — assertion đó
    /// PHẢI đỏ khi mutation đó được áp lại.</para>
    /// </summary>
    [Fact]
    public void Mime_BuildMailMessage_TruyenDungBienVaoDungBan_KhongLanLonStartsAtVoiExpiresAt()
    {
        var expiresAt = new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Utc);
        var startsAt = new DateTime(2026, 9, 10, 2, 0, 0, DateTimeKind.Utc);   // 09:00 giờ VN, 10/09/2026

        var parts = ParseParts(WriteEml(
            expiresAt: expiresAt, startsAt: startsAt, orgName: "Công ty Acme",
            faceVerifyEnabled: true, timeLimitMinutes: 45));

        var plain = parts.Single(p => p.ContentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)).Body;
        var html = parts.Single(p => p.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)).Body;

        foreach (var body in new[] { plain, html })
        {
            // Giờ MỞ (startsAt) — đúng vai, đúng mốc.
            Assert.Contains("10/09/2026", body);
            Assert.Contains("09:00", body);
            // Hạn CHÓT (expiresAt) — đúng vai, đúng mốc, ĐÚNG định dạng (UTC, không phải giờ VN).
            Assert.Contains("2026-12-31 23:59 UTC", body);
            // Đối chứng ngược: mốc của bên kia KHÔNG được lọt vào — nếu hai tham số bị hoán, ngày
            // 31/12/2026 sẽ xuất hiện dưới định dạng dd/MM/yyyy (dòng "Phỏng vấn mở từ").
            Assert.DoesNotContain("31/12/2026", body);
        }

        // 3 trường B4 còn lại cũng phải TỚI ĐÚNG BẢN — cả hai, không chỉ một.
        Assert.Contains("45 phút", plain);
        Assert.Contains("cần camera và micro", plain);
        Assert.Contains("Công ty Acme", plain);
        Assert.Contains("45 phút", html);
        Assert.Contains("cần camera và micro", html);
        Assert.Contains(System.Text.Encodings.Web.HtmlEncoder.Default.Encode("Công ty Acme"), html);
    }

    /// <summary>
    /// KHE NỐI — 4 tham số <c>DateTime?</c> của <c>BuildMailMessage</c> (<c>expiresAt</c>,
    /// <c>slotStartsAt</c>, <c>slotEndsAt</c>, <c>startsAt</c>) CÙNG KIỂU nên trình biên dịch không
    /// cản được một lần hoán vị giữa chúng — test trước chỉ cô lập <c>startsAt</c>↔<c>expiresAt</c>
    /// (slot để null). Test này seed CẢ BA mốc campaign-wide/slot cùng lúc, mỗi mốc một giá trị phân
    /// biệt được, để một hoán vị liên quan tới <c>slotStartsAt</c>/<c>slotEndsAt</c> cũng lộ ra.
    /// </summary>
    [Fact]
    public void Mime_BuildMailMessage_KhungGioSlot_KhongLanLonVoiGioCampaignMo()
    {
        var slotStarts = new DateTime(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);   // 08:00 VN 01/10
        var slotEnds = new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);     // 09:00 VN 01/10
        var campaignOpens = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc); // 10:00 VN 20/09

        var parts = ParseParts(WriteEmlWithSlot(slotStarts, slotEnds, campaignOpens));

        var plain = parts.Single(p => p.ContentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)).Body;
        var html = parts.Single(p => p.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)).Body;

        foreach (var body in new[] { plain, html })
        {
            // Khung giờ SLOT (per-invitation) — đúng ngày, đúng khoảng giờ.
            Assert.Contains("08:00–09:00, 01/10/2026", body);
            // Giờ campaign MỞ — mốc RIÊNG, KHÔNG lẫn vào dòng slot.
            Assert.Contains("20/09/2026", body);
        }
    }

    // Overload seed đủ 3 mốc DateTime? cùng lúc (slot + campaign-open) — WriteEml() gốc không có
    // slotStartsAt/slotEndsAt vì 4 test cũ không cần; thêm overload riêng thay vì đổi chữ ký gốc.
    private static string WriteEmlWithSlot(DateTime slotStartsAt, DateTime slotEndsAt, DateTime startsAt)
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
                "noreply@isas.test", "candidate@isas.test", "Backend Q3", "https://fe.test/invite/tok",
                null, slotStartsAt, slotEndsAt, startsAt);
            client.Send(message);
            return File.ReadAllText(Directory.GetFiles(pickup, "*.eml").Single());
        }
        finally
        {
            Directory.Delete(pickup, recursive: true);
        }
    }

    /// <summary>
    /// KHE NỐI — <c>faceVerifyEnabled</c>/<c>timeLimitMinutes</c> khác KIỂU với 4 tham số
    /// <c>DateTime?</c> nên không hoán vị được với chúng, nhưng vẫn có thể bị BỎ SÓT ở một trong hai
    /// lời gọi (Plain hoặc Html) mà build vẫn xanh (tham số có default). Test cô lập: KHÔNG có
    /// startsAt/slot/expiresAt/orgName — chỉ 2 trường này — để loại trừ khả năng chúng "vô tình" xuất
    /// hiện nhờ card khác.
    /// </summary>
    [Fact]
    public void Mime_BuildMailMessage_FaceVerifyVaThoiLuong_ToiCaHaiBan_DuKhiKhongCoMocThoiGianNao()
    {
        var parts = ParseParts(WriteEml(faceVerifyEnabled: true, timeLimitMinutes: 30));

        var plain = parts.Single(p => p.ContentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)).Body;
        var html = parts.Single(p => p.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)).Body;

        Assert.Contains("30 phút · cần camera và micro", plain);
        Assert.Contains("30 phút · cần camera và micro", html);
        // Không seed startsAt/slot ⇒ không được lộ dòng "Phỏng vấn mở từ"/"Khung giờ phỏng vấn".
        Assert.DoesNotContain("Phỏng vấn mở từ", plain);
        Assert.DoesNotContain("Khung giờ phỏng vấn", plain);
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
