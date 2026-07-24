using System.Text.Json;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// E7 — CreditEventHandler.HandleAsync: route event Interview → tiêu/nhả credit.
// Test trực tiếp handler (routing-key + json giả, KHÔNG cần RabbitMQ thật — như task yêu cầu).
//  (a) session.scored   → ConsumeAsync đúng sessionId, đúng 1 lần (Release không gọi).
//  (b) session.abandoned → ReleaseAsync đúng sessionId (Consume không gọi).
//  (c) key lạ            → bỏ qua (không gọi Consume/Release).
//  (d) gọi lại cùng session (redeliver) → idempotent: KHÔNG trừ/hoàn kép (service thật + SQLite).
public class CreditEventHandlerTests
{
    private static CreditEventHandler NewHandler(
        ICreditAccountService credits, DateTime? consumeFromUtc = null) =>
        new(credits, Mock.Of<ILogger<CreditEventHandler>>(),
            Options.Create(new OrphanReconcileSettings { ConsumeFromUtc = consumeFromUtc }));

    private static string ScoredJson(Guid sessionId, Guid? campaignId = null) =>
        JsonSerializer.Serialize(new SessionScoredMessage
        {
            SessionId = sessionId,
            CampaignId = campaignId,
            CandidateId = Guid.NewGuid(),
            TotalScore = 80m,
            ScoredAt = DateTime.UtcNow
        });

    private static string AbandonedJson(Guid sessionId, string reason = "expired_no_answer") =>
        JsonSerializer.Serialize(new SessionAbandonedMessage
        {
            SessionId = sessionId,
            CampaignId = null,
            CandidateId = Guid.NewGuid(),
            Reason = reason,
            AbandonedAt = DateTime.UtcNow
        });

    // (a) session.scored → ConsumeAsync(sessionId) đúng 1 lần; ReleaseAsync không gọi.
    [Fact]
    public async Task SessionScored_GoiConsume_DungSessionId_Mot_Lan()
    {
        var sessionId = Guid.NewGuid();
        var credits = new Mock<ICreditAccountService>();
        credits.Setup(c => c.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(ConsumeResult.Consumed(Guid.NewGuid()));

        await NewHandler(credits.Object)
            .HandleAsync(CreditEventHandler.SessionScoredRoutingKey, ScoredJson(sessionId));

        credits.Verify(c => c.ConsumeAsync(sessionId, It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(c => c.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (b) session.abandoned → ReleaseAsync(sessionId) đúng 1 lần; ConsumeAsync không gọi.
    [Fact]
    public async Task SessionAbandoned_GoiRelease_DungSessionId()
    {
        var sessionId = Guid.NewGuid();
        var credits = new Mock<ICreditAccountService>();
        credits.Setup(c => c.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(ReleaseResult.Released(Guid.NewGuid()));

        await NewHandler(credits.Object)
            .HandleAsync(CreditEventHandler.SessionAbandonedRoutingKey, AbandonedJson(sessionId));

        credits.Verify(c => c.ReleaseAsync(sessionId, It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(c => c.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // PONR1 Risk④: sau cutover, abandon không được hoàn chỗ giữ nếu inline-consume tại Ready bị lỗi.
    // Giữ Reserved để R1 consume bù; nếu release ở đây sẽ mở lại vòng sinh câu hỏi AI miễn phí.
    [Fact]
    public async Task SessionAbandoned_SauPONRCutover_ReservationConReserved_KhongRelease()
    {
        var sessionId = Guid.NewGuid();
        var mark = DateTime.UtcNow.AddMinutes(-1);
        var credits = new Mock<ICreditAccountService>(MockBehavior.Strict);
        credits.Setup(c => c.GetReservationGateSnapshotAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReservationGateSnapshot(true, mark));

        await NewHandler(credits.Object, mark)
            .HandleAsync(CreditEventHandler.SessionAbandonedRoutingKey, AbandonedJson(sessionId));

        credits.Verify(c => c.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        credits.Verify(c => c.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // generation_failed luôn xảy ra trước Ready/materialize, nên dù reservation tạo sau cutover vẫn phải hoàn.
    [Fact]
    public async Task SessionAbandoned_GenerationFailed_SauPONRCutover_VanRelease()
    {
        var sessionId = Guid.NewGuid();
        var credits = new Mock<ICreditAccountService>(MockBehavior.Strict);
        credits.Setup(c => c.ReleaseAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReleaseResult.Released(Guid.NewGuid()));

        await NewHandler(credits.Object, DateTime.UtcNow.AddMinutes(-1))
            .HandleAsync(CreditEventHandler.SessionAbandonedRoutingKey,
                AbandonedJson(sessionId, "generation_failed"));

        credits.Verify(c => c.GetReservationGateSnapshotAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        credits.Verify(c => c.ReleaseAsync(sessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // (c) routing key lạ → bỏ qua, KHÔNG gọi Consume/Release.
    [Fact]
    public async Task KeyLa_BoQua_KhongGoiGiCa()
    {
        var credits = new Mock<ICreditAccountService>(MockBehavior.Strict);

        await NewHandler(credits.Object)
            .HandleAsync("session.created", ScoredJson(Guid.NewGuid()));

        credits.Verify(c => c.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        credits.Verify(c => c.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // (d1) session.scored gửi lại 2 lần (redeliver) → idempotent: đúng 1 credit_transactions(Consume,−1),
    //      remaining/reserved không trừ kép (dùng CreditAccountService thật + SQLite, mỗi lần 1 scope mới).
    [Fact]
    public async Task SessionScored_GuiLai2Lan_Idempotent_KhongTruKep()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);
        // reserve trước (điều kiện vào bài): remaining 5→4, reserved 0→1.
        await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.User, userId, sessionId);

        var json = ScoredJson(sessionId);
        await NewHandler(new CreditAccountService(tdb.NewContext())).HandleAsync(CreditEventHandler.SessionScoredRoutingKey, json);
        await NewHandler(new CreditAccountService(tdb.NewContext())).HandleAsync(CreditEventHandler.SessionScoredRoutingKey, json);

        using var read = tdb.NewContext();
        Assert.Equal(1, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId)); // đúng 1 bút toán
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(4, acc.RemainingCredits);  // reserve trừ 1; consume không đụng remaining
        Assert.Equal(0, acc.ReservedCredits);   // reserved −1 đúng 1 lần
        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Consumed, reservation.Status);
    }

    // (d2) session.abandoned gửi lại 2 lần → idempotent: hoàn chỗ giữ đúng 1 lần (KHÔNG hoàn kép),
    //      KHÔNG ghi ledger (release không tiêu credit).
    [Fact]
    public async Task SessionAbandoned_GuiLai2Lan_Idempotent_KhongHoanKep()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);
        await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.User, userId, sessionId); // 5→4, reserved 1

        var json = AbandonedJson(sessionId);
        await NewHandler(new CreditAccountService(tdb.NewContext())).HandleAsync(CreditEventHandler.SessionAbandonedRoutingKey, json);
        await NewHandler(new CreditAccountService(tdb.NewContext())).HandleAsync(CreditEventHandler.SessionAbandonedRoutingKey, json);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(5, acc.RemainingCredits);  // hoàn chỗ giữ đúng 1 lần (4→5), không +2
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId)); // release không ghi ledger
        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Released, reservation.Status);
    }

    // (d3) out-of-order: đã Consumed rồi nhận session.abandoned → KHÔNG hoàn oan (absorbing PAY-11).
    [Fact]
    public async Task Consumed_RoiNhanAbandoned_KhongHoanOan()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);
        await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.User, userId, sessionId); // 5→4

        await NewHandler(new CreditAccountService(tdb.NewContext()))
            .HandleAsync(CreditEventHandler.SessionScoredRoutingKey, ScoredJson(sessionId));   // consume → 4, reserved 0
        await NewHandler(new CreditAccountService(tdb.NewContext()))
            .HandleAsync(CreditEventHandler.SessionAbandonedRoutingKey, AbandonedJson(sessionId)); // absorbed no-op

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(4, acc.RemainingCredits);  // KHÔNG bị hoàn lại thành 5
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(1, await read.CreditTransactions.CountAsync(t => t.SessionId == sessionId)); // vẫn 1 bút toán Consume
        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Consumed, reservation.Status);
    }

    private static async Task<CreditAccount> SeedAccountAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = 0,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(acc);
        await tdb.Db.SaveChangesAsync();
        return acc;
    }
}
