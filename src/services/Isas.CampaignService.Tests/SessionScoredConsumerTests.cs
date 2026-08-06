using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Khoá hai thứ trước giờ chỉ "verify live": topology DLX và định tuyến theo routing key.
///
/// Vì sao đáng viết: sai ở đây KHÔNG làm test nào đỏ và KHÔNG làm service chết — nó chỉ khiến
/// message biến mất trong im lặng (mất ranking của ứng viên) hoặc consumer chết vòng lặp 406.
/// Cả hai chỉ lộ ra khi đã chảy máu trên production.
/// </summary>
public class SessionScoredConsumerTests
{
    private const string Exchange = "interview.events";
    private const string Queue = "campaign.ranking";
    private const string Dlx = "campaign.ranking.dlx";
    private const string Dlq = "campaign.ranking.dead";

    private static SessionScoredConsumer NewConsumer(bool releaseOnAbandoned)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Membership:ReleaseOnAbandoned"] = releaseOnAbandoned ? "true" : "false"
            }).Build();

        return new SessionScoredConsumer(
            config,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SessionScoredConsumer>.Instance);
    }

    // ── Topology ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Topology_KhaiDlxVaDlq_BindBangTenQueueChinh()
    {
        var channel = new Mock<IChannel>(MockBehavior.Loose);

        await NewConsumer(false).DeclareTopologyAsync(channel.Object, default);

        channel.Verify(c => c.ExchangeDeclareAsync(
            Dlx, ExchangeType.Direct, true, false, null, false, false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.QueueDeclareAsync(
            Dlq, true, false, false, null, false, false, It.IsAny<CancellationToken>()), Times.Once);

        // Routing key phải là TÊN QUEUE CHÍNH — khớp `dead-letter-routing-key` của policy ops.
        // Thiếu vế này thì message chết giữ key gốc `session.scored`, direct exchange không khớp
        // ⇒ rơi lần thứ hai: vá mà vẫn mất message, triệu chứng y hệt lúc chưa vá.
        channel.Verify(c => c.QueueBindAsync(
            Dlq, Dlx, Queue, null, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Topology_QueueChinh_KHONG_DUOC_khai_kem_arguments()
    {
        var channel = new Mock<IChannel>(MockBehavior.Loose);

        await NewConsumer(true).DeclareTopologyAsync(channel.Object, default);

        // 🔴 Queue chính đã tồn tại trên production với arguments rỗng. Khai lại kèm x-dead-letter-*
        // ⇒ 406 PRECONDITION_FAILED ⇒ consumer chết vòng lặp reconnect (bẫy payment.credit, S6 đợt 9).
        channel.Verify(c => c.QueueDeclareAsync(
            Queue, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.Is<IDictionary<string, object?>?>(a => a == null || a.Count == 0),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Topology_BindAbandoned_ChiKhiBatCo(bool releaseOnAbandoned)
    {
        var channel = new Mock<IChannel>(MockBehavior.Loose);

        await NewConsumer(releaseOnAbandoned).DeclareTopologyAsync(channel.Object, default);

        channel.Verify(c => c.QueueBindAsync(
                Queue, Exchange, "session.abandoned", null, false, It.IsAny<CancellationToken>()),
            releaseOnAbandoned ? Times.Once() : Times.Never());

        // Bind session.scored là vô điều kiện — cờ chỉ chi phối nhánh abandoned.
        channel.Verify(c => c.QueueBindAsync(
            Queue, Exchange, "session.scored", null, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Định tuyến ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_CoTat_EventAbandoned_KHONG_goi_handler_va_KHONG_nem()
    {
        var handler = new Mock<IRankingEventHandler>(MockBehavior.Strict);
        var json = JsonSerializer.Serialize(new SessionAbandonedMessage
        {
            SessionId = Guid.NewGuid(), CampaignId = Guid.NewGuid(), CandidateId = Guid.NewGuid()
        });

        // Không ném ⇒ caller ACK. Nếu đổi thành nack-requeue thì message quay vòng vô tận,
        // vì lần giao lại vẫn rơi đúng nhánh cờ-tắt này.
        await NewConsumer(false).DispatchAsync("session.abandoned", json, handler.Object, default);

        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatch_CoBat_EventAbandoned_Goi_handler()
    {
        var handler = new Mock<IRankingEventHandler>();
        var evt = new SessionAbandonedMessage
        {
            SessionId = Guid.NewGuid(), CampaignId = Guid.NewGuid(), CandidateId = Guid.NewGuid()
        };

        await NewConsumer(true).DispatchAsync(
            "session.abandoned", JsonSerializer.Serialize(evt), handler.Object, default);

        handler.Verify(h => h.HandleSessionAbandonedAsync(
            It.Is<SessionAbandonedMessage>(m => m.SessionId == evt.SessionId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatch_EventScored_Luon_goi_handler_du_co_tat()
    {
        var handler = new Mock<IRankingEventHandler>();
        var evt = new SessionScoredMessage
        {
            SessionId = Guid.NewGuid(), CampaignId = Guid.NewGuid(),
            CandidateId = Guid.NewGuid(), TotalScore = 80m
        };

        await NewConsumer(false).DispatchAsync(
            "session.scored", JsonSerializer.Serialize(evt), handler.Object, default);

        handler.Verify(h => h.HandleSessionScoredAsync(
            It.Is<SessionScoredMessage>(m => m.SessionId == evt.SessionId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatch_RoutingKeyLa_KHONG_nem_de_khoi_nghen_queue()
    {
        var handler = new Mock<IRankingEventHandler>(MockBehavior.Strict);

        await NewConsumer(true).DispatchAsync("session.gi.do", "{}", handler.Object, default);

        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatch_JsonRac_NEM_de_caller_nack()
    {
        var handler = new Mock<IRankingEventHandler>(MockBehavior.Strict);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewConsumer(true).DispatchAsync("session.scored", "khong-phai-json", handler.Object, default));
    }
}
