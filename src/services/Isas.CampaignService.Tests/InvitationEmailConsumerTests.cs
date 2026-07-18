using System.Text;
using System.Text.Json;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// D1 (consumer) + DB2b (dedup): InvitationEmailConsumer.ProcessMessageAsync — deserialize
/// InvitationEmailJob, dedup theo <c>email_sent_at</c>, compose magic-link {baseUrl}/invitations/{token},
/// gọi ICampaignEmailSender, rồi set <c>email_sent_at</c>. Test trực tiếp logic 1 message (mock sender +
/// DbContext SQLite thật) — KHÔNG cần broker/SMTP thật.
/// </summary>
public class InvitationEmailConsumerTests
{
    private static InvitationEmailConsumer NewConsumer(IDictionary<string, string?> config) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(config).Build(),
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<InvitationEmailConsumer>.Instance);

    // Mirror publisher/outbox: JsonSerializer.Serialize(job) mặc định (PascalCase wire keys).
    private static byte[] SerializeJob(InvitationEmailJob job) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job));

    // Seed campaign (Active, không xoá → soft-delete filter cho qua) + invitation → trả invitation.
    private static CampaignInvitation SeedInvitation(
        CampaignTestDb tdb, string email, string token, DateTime? emailSentAt = null)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        var inv = new CampaignInvitation
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            TokenHash = InvitationTokens.Hash(token),   // DB23 — DB giữ hash; job/email mang bản thô
            Email = email,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            SentAt = DateTime.UtcNow,       // producer-side: đã vào outbox
            EmailSentAt = emailSentAt       // consumer-side: null = chưa gửi
        };
        tdb.Db.CampaignInvitations.Add(inv);
        tdb.Db.SaveChanges();
        return inv;
    }

    [Fact]
    public async Task ProcessMessage_Deserialize_ComposeLink_GoiSender_SetEmailSentAt()
    {
        using var tdb = new CampaignTestDb();
        var inv = SeedInvitation(tdb, "cand@acme.test", "tok-123");
        var expires = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var job = new InvitationEmailJob(inv.Id, inv.CampaignId, "cand@acme.test", "tok-123", "Backend Q3", expires);

        var sender = new Mock<ICampaignEmailSender>();
        var consumer = NewConsumer(new Dictionary<string, string?>
        {
            ["Invitation:BaseUrl"] = "https://gw.test"
        });

        await consumer.ProcessMessageAsync(SerializeJob(job), sender.Object, tdb.NewContext(), default);

        // Sender được gọi đúng 1 lần với email + link magic-link mong đợi (deserialize PascalCase OK).
        sender.Verify(s => s.SendInvitationEmailAsync(
            "cand@acme.test",
            "Backend Q3",
            "https://gw.test/invitations/tok-123",
            expires,
            It.IsAny<CancellationToken>()), Times.Once);
        sender.VerifyNoOtherCalls();

        // DB2b — đánh dấu đã gửi (chống trùng khi redeliver).
        using var check = tdb.NewContext();
        var saved = await check.CampaignInvitations.FirstAsync(i => i.Id == inv.Id);
        Assert.NotNull(saved.EmailSentAt);
    }

    [Fact]
    public async Task ProcessMessage_FallbackGatewayUrl_KhiThieuInvitationBaseUrl()
    {
        using var tdb = new CampaignTestDb();
        var inv = SeedInvitation(tdb, "b@acme.test", "abc");
        var job = new InvitationEmailJob(inv.Id, inv.CampaignId, "b@acme.test", "abc", "Frontend", null);

        var sender = new Mock<ICampaignEmailSender>();
        // Chỉ có Gateway:Url (không có Invitation:BaseUrl) + có '/' cuối → phải trim tránh '//'.
        var consumer = NewConsumer(new Dictionary<string, string?>
        {
            ["Gateway:Url"] = "https://gateway.test/"
        });

        await consumer.ProcessMessageAsync(SerializeJob(job), sender.Object, tdb.NewContext(), default);

        sender.Verify(s => s.SendInvitationEmailAsync(
            "b@acme.test",
            "Frontend",
            "https://gateway.test/invitations/abc",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_BaseUrlRong_KhiThieuCauHinh()
    {
        using var tdb = new CampaignTestDb();
        var inv = SeedInvitation(tdb, "c@acme.test", "xyz");
        var job = new InvitationEmailJob(inv.Id, inv.CampaignId, "c@acme.test", "xyz", "QA", null);

        var sender = new Mock<ICampaignEmailSender>();
        var consumer = NewConsumer(new Dictionary<string, string?>());

        await consumer.ProcessMessageAsync(SerializeJob(job), sender.Object, tdb.NewContext(), default);

        // Thiếu Invitation:BaseUrl + Gateway:Url → baseUrl rỗng, link vẫn có phần path (relative).
        sender.Verify(s => s.SendInvitationEmailAsync(
            "c@acme.test",
            "QA",
            "/invitations/xyz",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_BodyNull_KhongGoiSender()
    {
        using var tdb = new CampaignTestDb();
        var sender = new Mock<ICampaignEmailSender>();
        var consumer = NewConsumer(new Dictionary<string, string?>());

        // Payload "null" → deserialize ra null → bỏ qua (không throw, không gọi sender/DB).
        await consumer.ProcessMessageAsync(Encoding.UTF8.GetBytes("null"), sender.Object, tdb.NewContext(), default);

        sender.Verify(s => s.SendInvitationEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // DB2b — dedup: deliver lần 2 khi email_sent_at đã set → KHÔNG gửi trùng (vẫn ack, không throw).
    [Fact]
    public async Task ProcessMessage_DaGuiRoi_BoTrung_KhongGoiSender()
    {
        using var tdb = new CampaignTestDb();
        var alreadySent = DateTime.UtcNow.AddMinutes(-5);
        var inv = SeedInvitation(tdb, "dup@acme.test", "tok-dup", emailSentAt: alreadySent);
        var job = new InvitationEmailJob(inv.Id, inv.CampaignId, "dup@acme.test", "tok-dup", "Backend", null);

        var sender = new Mock<ICampaignEmailSender>();
        var consumer = NewConsumer(new Dictionary<string, string?> { ["Invitation:BaseUrl"] = "https://gw.test" });

        // Redeliver — không throw (caller vẫn ack), không gọi sender (bỏ trùng).
        await consumer.ProcessMessageAsync(SerializeJob(job), sender.Object, tdb.NewContext(), default);

        sender.Verify(s => s.SendInvitationEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);

        // email_sent_at giữ mốc cũ (không đè).
        using var check = tdb.NewContext();
        var saved = await check.CampaignInvitations.FirstAsync(i => i.Id == inv.Id);
        Assert.Equal(alreadySent, saved.EmailSentAt);
    }

    // DB2b — gửi lần 1 rồi deliver lại (mô phỏng at-least-once) → chỉ gửi 1 lần tổng cộng.
    [Fact]
    public async Task ProcessMessage_Redeliver_ChiGui1Lan()
    {
        using var tdb = new CampaignTestDb();
        var inv = SeedInvitation(tdb, "once@acme.test", "tok-once");
        var job = new InvitationEmailJob(inv.Id, inv.CampaignId, "once@acme.test", "tok-once", "Backend", null);
        var body = SerializeJob(job);

        var sender = new Mock<ICampaignEmailSender>();
        var consumer = NewConsumer(new Dictionary<string, string?> { ["Invitation:BaseUrl"] = "https://gw.test" });

        // Lần 1: gửi + set email_sent_at. Lần 2 (redeliver): dedup bỏ qua.
        await consumer.ProcessMessageAsync(body, sender.Object, tdb.NewContext(), default);
        await consumer.ProcessMessageAsync(body, sender.Object, tdb.NewContext(), default);

        sender.Verify(s => s.SendInvitationEmailAsync(
            "once@acme.test", "Backend", It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // DB2b — invitation không tồn tại (đã xoá / campaign soft-delete) → bỏ qua (không gửi, không throw → ack).
    [Fact]
    public async Task ProcessMessage_InvitationKhongTonTai_KhongGoiSender()
    {
        using var tdb = new CampaignTestDb();
        var job = new InvitationEmailJob(Guid.NewGuid(), Guid.NewGuid(), "ghost@acme.test", "tok", "Backend", null);

        var sender = new Mock<ICampaignEmailSender>();
        var consumer = NewConsumer(new Dictionary<string, string?> { ["Invitation:BaseUrl"] = "https://gw.test" });

        await consumer.ProcessMessageAsync(SerializeJob(job), sender.Object, tdb.NewContext(), default);

        sender.Verify(s => s.SendInvitationEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
