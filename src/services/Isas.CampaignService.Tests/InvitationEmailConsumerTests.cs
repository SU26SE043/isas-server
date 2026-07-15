using System.Text;
using System.Text.Json;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// D1 (consumer): InvitationEmailConsumer.ProcessMessageAsync — deserialize InvitationEmailJob,
/// compose magic-link {baseUrl}/invitations/{token}, gọi ICampaignEmailSender.
/// Test trực tiếp logic 1 message (mock sender) — KHÔNG cần broker/SMTP thật.
/// </summary>
public class InvitationEmailConsumerTests
{
    private static InvitationEmailConsumer NewConsumer(IDictionary<string, string?> config) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(config).Build(),
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<InvitationEmailConsumer>.Instance);

    // Mirror publisher: JsonSerializer.Serialize(job) mặc định (PascalCase wire keys).
    private static byte[] SerializeJob(InvitationEmailJob job) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job));

    [Fact]
    public async Task ProcessMessage_Deserialize_ComposeLink_GoiSender()
    {
        var invitationId = Guid.NewGuid();
        var job = new InvitationEmailJob(
            invitationId, Guid.NewGuid(), "cand@acme.test", "tok-123", "Backend Q3",
            new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc));

        var sender = new Mock<ICampaignEmailSender>();
        var consumer = NewConsumer(new Dictionary<string, string?>
        {
            ["Invitation:BaseUrl"] = "https://gw.test"
        });

        await consumer.ProcessMessageAsync(SerializeJob(job), sender.Object, default);

        // Sender được gọi đúng 1 lần với email + link magic-link mong đợi (deserialize PascalCase OK).
        sender.Verify(s => s.SendInvitationEmailAsync(
            "cand@acme.test",
            "Backend Q3",
            "https://gw.test/invitations/tok-123",
            job.ExpiresAt,
            It.IsAny<CancellationToken>()), Times.Once);
        sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessMessage_FallbackGatewayUrl_KhiThieuInvitationBaseUrl()
    {
        var job = new InvitationEmailJob(
            Guid.NewGuid(), Guid.NewGuid(), "b@acme.test", "abc", "Frontend", null);

        var sender = new Mock<ICampaignEmailSender>();
        // Chỉ có Gateway:Url (không có Invitation:BaseUrl) + có '/' cuối → phải trim tránh '//'.
        var consumer = NewConsumer(new Dictionary<string, string?>
        {
            ["Gateway:Url"] = "https://gateway.test/"
        });

        await consumer.ProcessMessageAsync(SerializeJob(job), sender.Object, default);

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
        var job = new InvitationEmailJob(
            Guid.NewGuid(), Guid.NewGuid(), "c@acme.test", "xyz", "QA", null);

        var sender = new Mock<ICampaignEmailSender>();
        var consumer = NewConsumer(new Dictionary<string, string?>());

        await consumer.ProcessMessageAsync(SerializeJob(job), sender.Object, default);

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
        var sender = new Mock<ICampaignEmailSender>();
        var consumer = NewConsumer(new Dictionary<string, string?>());

        // Payload "null" → deserialize ra null → bỏ qua (không throw, không gọi sender).
        await consumer.ProcessMessageAsync(Encoding.UTF8.GetBytes("null"), sender.Object, default);

        sender.Verify(s => s.SendInvitationEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
