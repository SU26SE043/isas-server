using System.Security.Claims;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SEC-2 face-verify gate + upload (D13: FLAG cho HR, KHÔNG auto-chặn). IFileService + IAiServiceFaceVerifyClient mock;
/// CampaignDbContext SQLite thật.
///  - enroll → set ReferenceImageKey + 204;
///  - check (mock signals) → mỗi tín hiệu → 1 cờ session_flags + 200;
///  - check không có ảnh tham chiếu → cờ identity_unverified;
///  - check khi FaceVerifyEnabled=false → no-op 204 (không upload/không gọi AI);
///  - ngoài thành viên → 403;
///  - StartInterview: FaceVerifyEnabled + chưa enroll → FaceEnrollRequired=true.
/// </summary>
public class FaceVerifyTests
{
    private static readonly Guid FixedSession = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── controller factory ─────────────────────────────────────────────────────────
    private static FaceVerifyController NewController(
        CampaignDbContext db, Guid? candidateId, IFileService file, IAiServiceFaceVerifyClient ai)
    {
        var controller = new FaceVerifyController(db, file, ai, Mock.Of<ILogger<FaceVerifyController>>());

        var claims = new List<Claim>();
        if (candidateId is not null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, candidateId.Value.ToString()));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return controller;
    }

    private static IFormFile FakeImage(string fileName = "face.jpg")
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "image", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    private static Campaign SeedCampaign(
        CampaignDbContext db, bool antiCheat = false, bool faceVerify = true)
    {
        var c = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active, antiCheat);
        c.FaceVerifyEnabled = faceVerify;
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    // Q4 — `sessionId` = buổi thi đã Start của thành viên này; route PHẢI trùng (mirror SessionFlagTests).
    // Mặc định FixedSession để test cũ dùng FixedSession không phải sửa gì.
    private static void SeedMember(
        CampaignDbContext db, Guid campaignId, Guid candidateId, string? referenceImageKey = null,
        Guid? sessionId = null)
    {
        // DB16 — membership (+ ReferenceImageKey) sống ở campaign_membership.
        db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            SessionId = sessionId ?? FixedSession,
            ReferenceImageKey = referenceImageKey,
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static Mock<IAiServiceFaceVerifyClient> AiReturning(params string[] signals)
    {
        var m = new Mock<IAiServiceFaceVerifyClient>();
        m.Setup(x => x.VerifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceVerifyResult(
                signals.Contains("multiple_faces") ? 2 : 1,
                signals.Length == 0,
                0.42f,
                signals.ToList()));
        return m;
    }

    // ── (1) face-check với tín hiệu mock → ghi đúng các cờ session_flags + 200 ───────
    [Fact]
    public async Task Check_MockedSignals_WritesSessionFlags_And200()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = FixedSession;
        var campaign = SeedCampaign(tdb.Db, antiCheat: false, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, referenceImageKey: "campaigns/ref.jpg");

        var ai = AiReturning("multiple_faces", "face_mismatch");
        var result = await NewController(tdb.NewContext(), candidateId, Mock.Of<IFileService>(), ai.Object)
            .Check(campaign.Id, sessionId, FakeImage(), default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<FaceCheckResponse>(ok.Value);
        Assert.Equal(2, body.FaceCount);
        Assert.False(body.Match);
        Assert.Equal(2, body.Signals.Count);

        using var check = tdb.NewContext();
        var flags = check.SessionFlags.Where(f => f.CampaignId == campaign.Id).ToList();
        Assert.Equal(2, flags.Count);
        Assert.Contains(flags, f => f.SignalType == "multiple_faces");
        Assert.Contains(flags, f => f.SignalType == "face_mismatch");
        Assert.All(flags, f =>
        {
            Assert.Equal(sessionId, f.SessionId);
            Assert.Equal(candidateId, f.CandidateId);
        });
    }

    // ── (1') match=true, không tín hiệu → 200, KHÔNG ghi cờ ─────────────────────────
    [Fact]
    public async Task Check_Match_NoSignals_NoFlags()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, referenceImageKey: "campaigns/ref.jpg");

        var ai = AiReturning();   // 0 signal → match=true
        var result = await NewController(tdb.NewContext(), candidateId, Mock.Of<IFileService>(), ai.Object)
            .Check(campaign.Id, FixedSession, FakeImage(), default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<FaceCheckResponse>(ok.Value);
        Assert.True(body.Match);
        Assert.Empty(body.Signals);

        using var check = tdb.NewContext();
        Assert.Equal(0, check.SessionFlags.Count(f => f.CampaignId == campaign.Id));
    }

    // ── (1'') AIService lỗi hạ tầng (timeout/5xx) → 502 có log, KHÔNG 500 trần ────────
    // Trước đây VerifyAsync ném thẳng ra ngoài action (không try/catch) → 500 mất log rõ ràng, không nhất
    // quán với mọi controller khác trong service (đều map DownstreamServiceException → 502).
    [Fact]
    public async Task Check_AiServiceThrows_Maps502_NotBare500()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, referenceImageKey: "campaigns/ref.jpg");

        var ai = new Mock<IAiServiceFaceVerifyClient>();
        ai.Setup(x => x.VerifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamServiceException("Không gọi được AIService face-verify."));

        var result = await NewController(tdb.NewContext(), candidateId, Mock.Of<IFileService>(), ai.Object)
            .Check(campaign.Id, FixedSession, FakeImage(), default);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    // ── (2) face-check không có ảnh tham chiếu → cờ identity_unverified, KHÔNG gọi AI ─
    [Fact]
    public async Task Check_NoReference_RecordsIdentityUnverified()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = FixedSession;
        var campaign = SeedCampaign(tdb.Db, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, referenceImageKey: null);   // chưa enroll

        var ai = AiReturning("face_mismatch");   // không nên được gọi
        var result = await NewController(tdb.NewContext(), candidateId, Mock.Of<IFileService>(), ai.Object)
            .Check(campaign.Id, sessionId, FakeImage(), default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<FaceCheckResponse>(ok.Value);
        Assert.Equal(new List<string> { "identity_unverified" }, body.Signals);

        ai.Verify(x => x.VerifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        using var check = tdb.NewContext();
        var flag = Assert.Single(check.SessionFlags.Where(f => f.CampaignId == campaign.Id));
        Assert.Equal("identity_unverified", flag.SignalType);
        Assert.Equal(sessionId, flag.SessionId);
    }

    // ── (3) FaceVerifyEnabled=false → no-op 204 (không upload, không gọi AI, không cờ) ─
    [Fact]
    public async Task Check_FaceVerifyDisabled_NoOp204()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, antiCheat: false, faceVerify: false);
        SeedMember(tdb.Db, campaign.Id, candidateId, referenceImageKey: "campaigns/ref.jpg");

        var file = new Mock<IFileService>();
        var ai = AiReturning("face_mismatch");
        var result = await NewController(tdb.NewContext(), candidateId, file.Object, ai.Object)
            .Check(campaign.Id, FixedSession, FakeImage(), default);

        Assert.IsType<NoContentResult>(result);
        file.Verify(x => x.UploadAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        ai.Verify(x => x.VerifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        using var check = tdb.NewContext();
        Assert.Equal(0, check.SessionFlags.Count(f => f.CampaignId == campaign.Id));
    }

    // ── (4) enroll → upload S3 + set ReferenceImageKey + 204 ────────────────────────
    [Fact]
    public async Task Enroll_SetsReferenceImageKey_204()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = FixedSession;
        var campaign = SeedCampaign(tdb.Db, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, candidateId, referenceImageKey: null);

        var file = new Mock<IFileService>();
        file.Setup(x => x.UploadAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IFormFile _, string key, CancellationToken _) => key);

        var expectedKey = $"campaigns/{campaign.Id}/candidates/{candidateId}/face-reference.jpg";
        var result = await NewController(tdb.NewContext(), candidateId, file.Object, Mock.Of<IAiServiceFaceVerifyClient>())
            .Enroll(campaign.Id, sessionId, FakeImage(), default);

        Assert.IsType<NoContentResult>(result);
        file.Verify(x => x.UploadAsync(It.IsAny<IFormFile>(), expectedKey, It.IsAny<CancellationToken>()), Times.Once);

        using var check = tdb.NewContext();
        var membership = Assert.Single(check.CampaignMemberships.Where(m => m.CampaignId == campaign.Id));
        Assert.Equal(expectedKey, membership.ReferenceImageKey);
    }

    // ── (5) ngoài thành viên campaign → 403 (không upload/không cờ) ─────────────────
    [Fact]
    public async Task Check_NonMember_403()
    {
        using var tdb = new CampaignTestDb();
        var member = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var campaign = SeedCampaign(tdb.Db, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, member, referenceImageKey: "campaigns/ref.jpg");

        var file = new Mock<IFileService>();
        var result = await NewController(tdb.NewContext(), outsider, file.Object, Mock.Of<IAiServiceFaceVerifyClient>())
            .Check(campaign.Id, Guid.NewGuid(), FakeImage(), default);

        Assert.IsType<ForbidResult>(result);
        file.Verify(x => x.UploadAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        using var check = tdb.NewContext();
        Assert.Equal(0, check.SessionFlags.Count(f => f.CampaignId == campaign.Id));
    }

    // ── 🔴 Q4 — THÀNH VIÊN cùng campaign gọi face-check trên buổi của NGƯỜI KHÁC → 403 ─────
    // Rộng hơn đường `flags`: sessionId đi vào CẢ khoá S3 ảnh live (`campaigns/{c}/sessions/{s}/...`)
    // LẪN session_flags ⇒ vừa cắm được cờ danh tính lên buổi nạn nhân, vừa nhét ảnh vào thư mục đó.
    [Fact]
    public async Task Q4_Check_ThanhVienKhac_BuoiCuaNguoiKhac_403_KhongUpload_KhongCo()
    {
        using var tdb = new CampaignTestDb();
        var attacker = Guid.NewGuid();
        var victim = Guid.NewGuid();
        var victimSession = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var campaign = SeedCampaign(tdb.Db, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, attacker, referenceImageKey: "campaigns/attacker.jpg");
        SeedMember(tdb.Db, campaign.Id, victim, referenceImageKey: "campaigns/victim.jpg",
            sessionId: victimSession);

        var file = new Mock<IFileService>();
        var ai = AiReturning("face_mismatch");
        var result = await NewController(tdb.NewContext(), attacker, file.Object, ai.Object)
            .Check(campaign.Id, victimSession, FakeImage(), default);

        Assert.IsType<ForbidResult>(result);
        file.Verify(x => x.UploadAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        ai.Verify(x => x.VerifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        using var check = tdb.NewContext();
        Assert.Equal(0, check.SessionFlags.Count(f => f.CampaignId == campaign.Id));
    }

    // Q4 — face-enroll dùng CHUNG helper nên cũng phải chặn: sessionId ở đó hiện chỉ nằm trong route và
    // không được dùng, nhưng để một tham số không ai kiểm trên đường ghi là đúng hình dạng lỗi vừa vá.
    [Fact]
    public async Task Q4_Enroll_BuoiCuaNguoiKhac_403_KhongUpload()
    {
        using var tdb = new CampaignTestDb();
        var attacker = Guid.NewGuid();
        var victim = Guid.NewGuid();
        var victimSession = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var campaign = SeedCampaign(tdb.Db, faceVerify: true);
        SeedMember(tdb.Db, campaign.Id, attacker, referenceImageKey: null);
        SeedMember(tdb.Db, campaign.Id, victim, referenceImageKey: null, sessionId: victimSession);

        var file = new Mock<IFileService>();
        var result = await NewController(tdb.NewContext(), attacker, file.Object, Mock.Of<IAiServiceFaceVerifyClient>())
            .Enroll(campaign.Id, victimSession, FakeImage(), default);

        Assert.IsType<ForbidResult>(result);
        file.Verify(x => x.UploadAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── (5') campaign không tồn tại → 404 ──────────────────────────────────────────
    [Fact]
    public async Task Enroll_CampaignNotFound_404()
    {
        using var tdb = new CampaignTestDb();
        var result = await NewController(
                tdb.NewContext(), Guid.NewGuid(), Mock.Of<IFileService>(), Mock.Of<IAiServiceFaceVerifyClient>())
            .Enroll(Guid.NewGuid(), FixedSession, FakeImage(), default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── (6) StartInterview: FaceVerifyEnabled + chưa enroll → FaceEnrollRequired=true ─
    [Fact]
    public async Task Start_FaceVerifyEnabled_NoReference_SetsFaceEnrollRequired()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        camp.FaceVerifyEnabled = true;
        tdb.Db.CampaignMemberships.Add(JoinedMembership(camp.Id, candidateId, referenceImageKey: null));
        await tdb.Db.SaveChangesAsync();

        var res = await NewParticipation(tdb.NewContext()).StartInterviewAsync(candidateId, camp.Id, default);

        Assert.True(res.FaceEnrollRequired);
    }

    // ── (6') StartInterview: đã enroll → FaceEnrollRequired=false ────────────────────
    [Fact]
    public async Task Start_FaceVerifyEnabled_WithReference_FaceEnrollRequiredFalse()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);
        camp.FaceVerifyEnabled = true;
        tdb.Db.CampaignMemberships.Add(
            JoinedMembership(camp.Id, candidateId, referenceImageKey: "campaigns/ref.jpg"));
        await tdb.Db.SaveChangesAsync();

        var res = await NewParticipation(tdb.NewContext()).StartInterviewAsync(candidateId, camp.Id, default);

        Assert.False(res.FaceEnrollRequired);
    }

    // ── (6'') StartInterview: face-verify tắt → FaceEnrollRequired=false dù chưa enroll ─
    [Fact]
    public async Task Start_FaceVerifyDisabled_FaceEnrollRequiredFalse()
    {
        using var tdb = new CampaignTestDb();
        var candidateId = Guid.NewGuid();
        var camp = ActiveCampaignWithQuestionAndCriterion(tdb);   // FaceVerifyEnabled default false
        tdb.Db.CampaignMemberships.Add(JoinedMembership(camp.Id, candidateId, referenceImageKey: null));
        await tdb.Db.SaveChangesAsync();

        var res = await NewParticipation(tdb.NewContext()).StartInterviewAsync(candidateId, camp.Id, default);

        Assert.False(res.FaceEnrollRequired);
    }

    // ── helpers cho ParticipationService (mirror ParticipationServiceTests) ──────────
    private static ParticipationService NewParticipation(CampaignDbContext db)
    {
        var auth = new Mock<IAuthProvisionClient>();
        var session = new Mock<ICampaignSessionClient>();
        session.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SessionQuestionInput>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignSessionResult(FixedSession, new List<SessionQuestion>
            {
                new(Guid.NewGuid(), 1, "Q1", 120)
            }));
        return new ParticipationService(db, auth.Object, session.Object, NullLogger<ParticipationService>.Instance);
    }

    private static Campaign ActiveCampaignWithQuestionAndCriterion(CampaignTestDb tdb)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.Questions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId,
            QuestionText = "Giải thích DI?", Source = QuestionSource.CustomHr,
            IsRequired = true, CreatedAt = DateTime.UtcNow
        });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Communication",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.Campaigns.Add(camp);
        return camp;
    }

    private static CampaignMembership JoinedMembership(Guid campaignId, Guid candidateId, string? referenceImageKey)
        => new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CandidateId = candidateId,
            ReferenceImageKey = referenceImageKey,
            Status = MembershipStatus.Joined,
            JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
}
