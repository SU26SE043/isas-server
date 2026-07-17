using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// DB18 — `POST /internal/sessions/exists`: Payment dò orphan reservation. Interview trả TẬP CON
/// sessionIds thực sự có row practice_sessions (bất kể status). Payment coi phần còn lại là orphan
/// (crash giữa reserve↔insert lúc Start) → release. Token guard giống `/campaign` (X-Internal-Token).
/// </summary>
public class InternalSessionsExistsTests
{
    // PracticeService thật với deps mock (chỉ dùng DbContext cho query exists).
    private static PracticeService BuildService(TestDb t)
    {
        var scoringNotifier = new Mock<ISessionScoringNotifier>();
        var reservation = new Mock<ICreditReservationClient>();
        return new PracticeService(t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            scoringNotifier.Object, reservation.Object,
            NullLogger<PracticeService>.Instance);
    }

    // Service — mix id tồn-tại/không → trả đúng TẬP CON tồn tại, bỏ id không tồn tại.
    [Fact]
    public async Task GetExistingSessionIds_TraDungTapConTonTai()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var s1 = TestDb.Session(candidate, SessionStatus.Ready);
        var s2 = TestDb.Session(candidate, SessionStatus.Scored);   // status khác nhau vẫn tính là tồn tại
        t.Db.PracticeSessions.AddRange(s1, s2);
        await t.Db.SaveChangesAsync();

        var missing = Guid.NewGuid();   // KHÔNG có row → orphan phía Payment
        var svc = BuildService(t);

        var existing = await svc.GetExistingSessionIdsAsync(new[] { s1.Id, missing, s2.Id });

        Assert.Equal(2, existing.Count);
        Assert.Contains(s1.Id, existing);
        Assert.Contains(s2.Id, existing);
        Assert.DoesNotContain(missing, existing);
    }

    // Service — không id nào tồn tại (mọi orphan) → rỗng.
    [Fact]
    public async Task GetExistingSessionIds_KhongIdNaoTonTai_TraRong()
    {
        using var t = new TestDb();
        var svc = BuildService(t);

        var existing = await svc.GetExistingSessionIdsAsync(new[] { Guid.NewGuid(), Guid.NewGuid() });

        Assert.Empty(existing);
    }

    // Service — input rỗng → rỗng (guard, không query).
    [Fact]
    public async Task GetExistingSessionIds_InputRong_TraRong()
    {
        using var t = new TestDb();
        var svc = BuildService(t);

        Assert.Empty(await svc.GetExistingSessionIdsAsync(Array.Empty<Guid>()));
    }

    // Controller — token đúng → 200 + existingIds = tập con tồn tại.
    [Fact]
    public async Task Controller_TokenDung_TraTapConTonTai()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var s1 = TestDb.Session(candidate, SessionStatus.InProgress);
        t.Db.PracticeSessions.Add(s1);
        await t.Db.SaveChangesAsync();

        var missing = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "test-internal-token"
        }).Build();

        var controller = new InternalSessionsController(
            BuildService(t), config, NullLogger<InternalSessionsController>.Instance);

        var req = new SessionExistsRequest(new[] { s1.Id, missing });
        var result = await controller.SessionsExist(req, token: "test-internal-token", default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SessionExistsResponse>(ok.Value);
        Assert.Single(body.ExistingIds);
        Assert.Contains(s1.Id, body.ExistingIds);
    }

    // Controller — token sai → 401 (không chạm service).
    [Fact]
    public async Task Controller_SaiToken_Tra401()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "test-internal-token"
        }).Build();

        var practice = new Mock<IPracticeService>();
        var controller = new InternalSessionsController(
            practice.Object, config, NullLogger<InternalSessionsController>.Instance);

        var req = new SessionExistsRequest(new[] { Guid.NewGuid() });
        var result = await controller.SessionsExist(req, token: "wrong-token", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        practice.Verify(p => p.GetExistingSessionIdsAsync(
            It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Controller — input null/rỗng → 200 rỗng (không chạm service).
    [Fact]
    public async Task Controller_InputRong_TraRong()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "test-internal-token"
        }).Build();

        var practice = new Mock<IPracticeService>();
        var controller = new InternalSessionsController(
            practice.Object, config, NullLogger<InternalSessionsController>.Instance);

        var result = await controller.SessionsExist(
            new SessionExistsRequest(null), token: "test-internal-token", default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SessionExistsResponse>(ok.Value);
        Assert.Empty(body.ExistingIds);
        practice.Verify(p => p.GetExistingSessionIdsAsync(
            It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
