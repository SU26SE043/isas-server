using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP4-B5 — dedup thư "chiến dịch mở sớm" (OpenedEarly) và chặn nó tới TRƯỚC thư mời.
///
/// <list type="bullet">
///   <item>(a) dedup theo <c>messageId</c> của outbox row — trong tiến trình (route A): cùng một
///     outbox row xử lý hai lần ⇒ gửi ĐÚNG một thư.</item>
///   <item>(b) chỉ gửi cho lời mời ĐÃ gửi được thư mời (<c>email_sent_at != null</c>) — lọc ở
///     producer (<c>StartEarlyAsync</c>) + guard ở consumer.</item>
///   <item>(c) sender ném quá N lần ⇒ THÔI requeue (không quay vòng nóng chặn cả hàng đợi thư mời).</item>
///   <item>(d) encoding tường minh — nội dung tiếng Việt có dấu đọc được (.eml thật).</item>
/// </list>
/// KHÔNG đụng nhánh THƯ MỜI (dedup bền vững qua <c>email_sent_at</c>).
/// </summary>
public class CampaignOpenedEarlyEmailCmp4B5Tests
{
    // ── Consumer branch ───────────────────────────────────────────────────────────────────────────

    private static InvitationEmailConsumer NewConsumer(IDictionary<string, string?>? config = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build(),
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<InvitationEmailConsumer>.Instance);

    private static CampaignInvitation SeedInvitation(
        CampaignTestDb tdb, bool emailSent = true, DateTime? revokedAt = null)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var inv = new CampaignInvitation
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id,
            TokenHash = InvitationTokens.Hash("tok"), Email = "cand@acme.test",
            ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow,
            SentAt = DateTime.UtcNow,
            EmailSentAt = emailSent ? DateTime.UtcNow.AddMinutes(-10) : null,
            RevokedAt = revokedAt,
        };
        tdb.Db.CampaignInvitations.Add(inv);
        tdb.Db.SaveChanges();
        return inv;
    }

    private static byte[] OpenedEarlyJob(CampaignInvitation inv) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new InvitationEmailJob(inv.Id, inv.CampaignId, inv.Email, string.Empty, "Backend Q3", inv.ExpiresAt,
            StartsAt: DateTime.UtcNow, OrgName: "Công ty Acme", Kind: "OpenedEarly",
            PreviousStartsAt: DateTime.UtcNow.AddDays(1))));

    private static Mock<ICampaignEmailSender> SenderMock()
    {
        var m = new Mock<ICampaignEmailSender>();
        m.Setup(s => s.SendCampaignOpenedEarlyEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return m;
    }

    private static void VerifyOpenedEarlySent(Mock<ICampaignEmailSender> sender, Times times) =>
        sender.Verify(s => s.SendCampaignOpenedEarlyEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), times);

    // ── (1) Cùng một outbox row (messageId) xử lý hai lần ⇒ gửi ĐÚNG một thư ─────────────────────
    [Fact]
    public async Task CungMessageId_XuLyHaiLan_GuiDungMotThu()
    {
        using var tdb = new CampaignTestDb();
        var inv = SeedInvitation(tdb);
        var body = OpenedEarlyJob(inv);
        var mid = Guid.NewGuid().ToString();
        var sender = SenderMock();
        var consumer = NewConsumer();

        await consumer.ProcessMessageAsync(body, mid, sender.Object, tdb.NewContext(), default);
        await consumer.ProcessMessageAsync(body, mid, sender.Object, tdb.NewContext(), default);   // redeliver

        VerifyOpenedEarlySent(sender, Times.Once());
    }

    // ── (2) messageId KHÁC (round mới) ⇒ vẫn gửi (dedup theo messageId, không theo invitation) ────
    [Fact]
    public async Task MessageIdKhac_VanGui()
    {
        using var tdb = new CampaignTestDb();
        var inv = SeedInvitation(tdb);
        var body = OpenedEarlyJob(inv);
        var sender = SenderMock();
        var consumer = NewConsumer();

        await consumer.ProcessMessageAsync(body, Guid.NewGuid().ToString(), sender.Object, tdb.NewContext(), default);
        await consumer.ProcessMessageAsync(body, Guid.NewGuid().ToString(), sender.Object, tdb.NewContext(), default);

        VerifyOpenedEarlySent(sender, Times.Exactly(2));
    }

    // ── (3) Lời mời CHƯA gửi thư mời (email_sent_at null) ⇒ KHÔNG nhận thư mở sớm ────────────────
    [Fact]
    public async Task ChuaGuiThuMoi_KhongNhanThuMoSom()
    {
        using var tdb = new CampaignTestDb();
        var inv = SeedInvitation(tdb, emailSent: false);
        var sender = SenderMock();

        await NewConsumer().ProcessMessageAsync(OpenedEarlyJob(inv), Guid.NewGuid().ToString(), sender.Object, tdb.NewContext(), default);

        VerifyOpenedEarlySent(sender, Times.Never());
    }

    // ── (4) Lời mời đã revoke ⇒ không nhận (giữ hành vi CMP3-B4) ─────────────────────────────────
    [Fact]
    public async Task DaRevoke_KhongNhan()
    {
        using var tdb = new CampaignTestDb();
        var inv = SeedInvitation(tdb, revokedAt: DateTime.UtcNow.AddMinutes(-1));
        var sender = SenderMock();

        await NewConsumer().ProcessMessageAsync(OpenedEarlyJob(inv), Guid.NewGuid().ToString(), sender.Object, tdb.NewContext(), default);

        VerifyOpenedEarlySent(sender, Times.Never());
    }

    // ── (5) Sender ném N+1 lần ⇒ N lần đầu rethrow (caller requeue), lần N+1 KHÔNG throw (rơi khỏi hàng đợi) ─
    [Fact]
    public async Task LoiLap_QuaTran_ThoiRequeue()
    {
        using var tdb = new CampaignTestDb();
        var inv = SeedInvitation(tdb);
        var body = OpenedEarlyJob(inv);
        var mid = Guid.NewGuid().ToString();

        var sender = new Mock<ICampaignEmailSender>();
        sender.Setup(s => s.SendCampaignOpenedEarlyEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SmtpException("SMTP down"));

        var consumer = NewConsumer(new Dictionary<string, string?> { ["OpenedEarlyEmail:MaxAttempts"] = "3" });

        // 2 lần đầu (< N=3) → rethrow ⇒ caller sẽ nack+requeue.
        await Assert.ThrowsAsync<SmtpException>(() =>
            consumer.ProcessMessageAsync(body, mid, sender.Object, tdb.NewContext(), default));
        await Assert.ThrowsAsync<SmtpException>(() =>
            consumer.ProcessMessageAsync(body, mid, sender.Object, tdb.NewContext(), default));

        // Lần 3 (= N) → nuốt lỗi, KHÔNG throw ⇒ caller ack ⇒ message rơi khỏi hàng đợi.
        await consumer.ProcessMessageAsync(body, mid, sender.Object, tdb.NewContext(), default);

        VerifyOpenedEarlySent(sender, Times.Exactly(3));   // thử gửi mỗi lần
    }

    // ── (5b) Nhánh THƯ MỜI KHÔNG bị đụng: sender ném ⇒ vẫn rethrow (caller requeue) ──────────────
    [Fact]
    public async Task NhanhThuMoi_KhongApTranThatBai()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var inv = new CampaignInvitation
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, TokenHash = InvitationTokens.Hash("t"),
            Email = "m@x.test", ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow,
        };
        tdb.Db.CampaignInvitations.Add(inv);
        tdb.Db.SaveChanges();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new InvitationEmailJob(inv.Id, inv.CampaignId, "m@x.test", "t", "BE", null)));

        var sender = new Mock<ICampaignEmailSender>();
        sender.Setup(s => s.SendInvitationEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SmtpException("SMTP down"));

        var consumer = NewConsumer(new Dictionary<string, string?>
        {
            ["Invitation:BaseUrl"] = "https://fe.test",
            ["OpenedEarlyEmail:MaxAttempts"] = "3",
        });
        var mid = Guid.NewGuid().ToString();

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<SmtpException>(() =>
                consumer.ProcessMessageAsync(body, mid, sender.Object, tdb.NewContext(), default));   // LUÔN throw
    }

    // ── Producer filter (StartEarlyAsync) ─────────────────────────────────────────────────────────

    private static CampaignSvc NewSvc(CampaignDbContext db)
    {
        var ent = new Mock<IEntitlementClient>();
        ent.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignEntitlement("t", "business", 1, 10, 200, true, true, true));
        return new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), entitlements: ent.Object);
    }

    // ── (6) StartEarlyAsync — lời mời chưa gửi thư mời ⇒ KHÔNG sinh outbox row mở sớm ────────────
    [Fact]
    public async Task StartEarly_LoiMoiChuaGuiThuMoi_KhongSinhOutbox()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.StartsAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        camp.ExpiresAt = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.AddRange(
            new CampaignInvitation { Id = Guid.NewGuid(), CampaignId = camp.Id, TokenHash = "h1",
                Email = "sent@x.test", ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow,
                EmailSentAt = DateTime.UtcNow.AddMinutes(-10) },
            new CampaignInvitation { Id = Guid.NewGuid(), CampaignId = camp.Id, TokenHash = "h2",
                Email = "notsent@x.test", ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow,
                EmailSentAt = null });
        tdb.Db.SaveChanges();

        await NewSvc(tdb.NewContext()).StartEarlyAsync(owner, owner, camp.Id, default);

        using var check = tdb.NewContext();
        var rows = await check.OutboxMessages.Where(m => m.CampaignId == camp.Id).ToListAsync();
        var row = Assert.Single(rows);
        var job = JsonSerializer.Deserialize<InvitationEmailJob>(row.Payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("sent@x.test", job.Email);
    }

    // ── (d) .eml thật — nội dung tiếng Việt có dấu đọc được ───────────────────────────────────────

    private static (string Subject, string Body) WriteAndReadOpenedEarlyEml()
    {
        var msg = CampaignEmailSender.BuildOpenedEarlyMessage(
            "noreply@isas.test", "cand@isas.test", "Kỹ sư Backend",
            previousStartsAt: new DateTime(2099, 2, 1, 2, 0, 0, DateTimeKind.Utc),
            newStartsAt: new DateTime(2099, 1, 15, 2, 0, 0, DateTimeKind.Utc),
            orgName: "Công ty Acme");

        var pickup = Path.Combine(Path.GetTempPath(), "isas-oe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pickup);
        try
        {
            using var client = new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = pickup,
            };
            using (msg) client.Send(msg);
            var eml = File.ReadAllText(Directory.GetFiles(pickup, "*.eml").Single());
            return (DecodeHeader(HeaderValue(eml, "Subject")), DecodeSinglePartBody(eml));
        }
        finally
        {
            Directory.Delete(pickup, recursive: true);
        }
    }

    private static string HeaderValue(string eml, string name)
    {
        var lines = eml.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) continue;
            var val = lines[i][(name.Length + 1)..].Trim();
            while (i + 1 < lines.Length && (lines[i + 1].StartsWith(' ') || lines[i + 1].StartsWith('\t')))
                val += lines[++i].Trim();
            return val;
        }
        return string.Empty;
    }

    // RFC 2047 encoded-word: =?utf-8?B?base64?=  hoặc  =?utf-8?Q?qp?=  (có thể nhiều word nối nhau).
    private static string DecodeHeader(string raw)
    {
        var sb = new StringBuilder();
        var rest = raw;
        while (true)
        {
            var start = rest.IndexOf("=?", StringComparison.Ordinal);
            if (start < 0) { sb.Append(rest); break; }
            sb.Append(rest[..start].Replace(" ", ""));   // whitespace giữa các encoded-word bị bỏ
            var end = rest.IndexOf("?=", start + 2, StringComparison.Ordinal);
            var word = rest[(start + 2)..end];
            var parts = word.Split('?');   // charset ? enc ? text
            var text = parts[2];
            sb.Append(parts[1].ToUpperInvariant() == "B"
                ? Encoding.UTF8.GetString(Convert.FromBase64String(text))
                : DecodeQuotedPrintable(text.Replace('_', ' ')));
            rest = rest[(end + 2)..];
        }
        return sb.ToString();
    }

    private static string DecodeSinglePartBody(string eml)
    {
        var norm = eml.Replace("\r\n", "\n");
        var split = norm.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = norm[..split];
        var body = norm[(split + 2)..].Trim();
        var cte = HeaderValue(eml, "Content-Transfer-Encoding").ToLowerInvariant();
        return cte switch
        {
            "base64" => Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace("\n", ""))),
            "quoted-printable" => DecodeQuotedPrintable(body),
            _ => body,
        };
    }

    private static string DecodeQuotedPrintable(string s)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (s[i] == '=' && i + 1 < s.Length && (s[i + 1] == '\n' || s[i + 1] == '\r'))
            {
                while (i + 1 < s.Length && (s[i + 1] == '\n' || s[i + 1] == '\r')) i++;
            }
            else
            {
                bytes.Add((byte)s[i]);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    [Fact]
    public void Eml_NoiDungTiengVietCoDau_DocDuoc()
    {
        var (subject, body) = WriteAndReadOpenedEarlyEml();

        Assert.Contains("Chiến dịch đã mở sớm", subject);
        Assert.Contains("Kỹ sư Backend", subject);

        Assert.Contains("Chiến dịch đánh giá bạn được mời", body);
        Assert.Contains("Vui lòng dùng lại liên kết trong email mời trước đó để tham gia.", body);
        Assert.Contains("Công ty Acme", body);
        Assert.DoesNotContain("?", body);   // không mojibake dấu hỏi
    }
}
