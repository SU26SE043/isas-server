using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Isas.CampaignService.Tests;

/// <summary>
/// ATT1-B2 — đếm lượt làm bài ở Start, rút đề theo lượt, bỏ qua sự kiện bỏ ngang của lượt cũ,
/// field mới cho ứng viên, thời lượng gửi sang Interview, Start KHÔNG trả nội dung câu.
/// Hợp đồng ATT1 [C6] [C7] [C8].
/// </summary>
public class ParticipationAttemptAtt1B2Tests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ── Bộ đầu vào CỐ ĐỊNH cho khoá "lượt 1 = đề cũ". Danh sách kỳ vọng SINH TỪ CODE CŨ (selector ở
    //    6f39b81, chép nguyên file vào một console project rồi chạy) — KHÔNG tính từ code mới, nếu không
    //    test chỉ khoá chính đầu ra của nó và không chứng minh được gì.
    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private static readonly Guid PoolCampaign = Guid.Parse("a0a0a0a0-1111-2222-3333-444455556666");
    private static readonly Guid PoolCandidate = Guid.Parse("c0c0c0c0-7777-8888-9999-aaaabbbbcccc");
    private static readonly Guid CritA = Guid.Parse("ca000000-0000-0000-0000-00000000000a");
    private static readonly Guid CritB = Guid.Parse("cb000000-0000-0000-0000-00000000000b");

    private static List<PoolQuestion> Pool1() => new()
    {
        new(G(1), "Q1", null, true,  "A"),
        new(G(2), "Q2", null, false, "A"),
        new(G(3), "Q3", null, false, "A"),
        new(G(4), "Q4", null, false, "B"),
        new(G(5), "Q5", null, true,  "B"),
        new(G(6), "Q6", null, false, "B"),
        new(G(7), "Q7", null, false, null),
        new(G(8), "Q8", null, false, null),
        new(G(9), "Q9", null, false, "A"),
        new(G(10), "Q10", null, false, "B"),
    };

    private static List<PoolQuestion> Pool2() => new()
    {
        new(G(11), "R1", null, false, null) { TargetCriterionIds = new[] { CritA } },
        new(G(12), "R2", null, false, null) { TargetCriterionIds = new[] { CritA } },
        new(G(13), "R3", null, false, null) { TargetCriterionIds = new[] { CritB } },
        new(G(14), "R4", null, false, null) { TargetCriterionIds = new[] { CritB } },
        new(G(15), "R5", null, false, null),
        new(G(16), "R6", null, false, "X"),
        new(G(17), "R7", null, false, null) { TargetCriterionIds = new[] { CritA, CritB } },
        new(G(18), "R8", null, false, null) { TargetCriterionIds = new[] { CritB } },
        new(G(19), "R9", null, false, "X"),
        new(G(20), "R10", null, false, null),
    };

    // Đầu ra của QuestionPoolSelector CŨ (6f39b81) trên đúng hai bộ trên.
    private static readonly Guid[] OldS1K5 = { G(7), G(5), G(9), G(4), G(1) };
    private static readonly Guid[] OldS2K3 = { G(17), G(13), G(15) };

    [Theory]
    [InlineData(null)]   // không truyền attemptNo (mọi caller cũ)
    [InlineData(1)]
    [InlineData(0)]      // membership cũ resume với attempt_count = 0 ⇒ vẫn đề lượt 1
    public void Selector_Luot1_TrungKhopDeCu_DanhSachIdSinhTuCodeCu(int? attemptNo)
    {
        List<PoolQuestion> Run(List<PoolQuestion> pool, int k) => attemptNo is int a
            ? QuestionPoolSelector.Select(pool, k, PoolCampaign, PoolCandidate, attemptNo: a)
            : QuestionPoolSelector.Select(pool, k, PoolCampaign, PoolCandidate);

        Assert.Equal(OldS1K5, Run(Pool1(), 5).Select(q => q.Id));
        Assert.Equal(OldS2K3, Run(Pool2(), 3).Select(q => q.Id));
    }

    // Pool 10 câu không bắt buộc, không nhóm — K = 3 còn nhiều chỗ để lượt sau rút khác lượt trước
    // (Pool1 có 2 câu bắt buộc ⇒ K = 3 chỉ còn 1 khe, quá chật để phân biệt hạt giống).
    private static List<PoolQuestion> Plain10() =>
        Enumerable.Range(31, 10).Select(i => new PoolQuestion(G(i), $"P{i}", null, false, null)).ToList();

    [Fact]
    public void Selector_Luot2_RutDeMoi_VaTaiLapDuoc()
    {
        var l1 = QuestionPoolSelector.Select(Plain10(), 3, PoolCampaign, PoolCandidate, attemptNo: 1).Select(q => q.Id).ToList();
        var l2 = QuestionPoolSelector.Select(Plain10(), 3, PoolCampaign, PoolCandidate, attemptNo: 2).Select(q => q.Id).ToList();
        var l2Again = QuestionPoolSelector.Select(Plain10(), 3, PoolCampaign, PoolCandidate, attemptNo: 2).Select(q => q.Id).ToList();
        var l3 = QuestionPoolSelector.Select(Plain10(), 3, PoolCampaign, PoolCandidate, attemptNo: 3).Select(q => q.Id).ToList();

        Assert.Equal(3, l2.Count);
        Assert.NotEqual(l1, l2);            // lượt làm lại ra đề khác
        Assert.Equal(l2, l2Again);          // vào lại lượt 2 ra đúng đề lượt 2
        Assert.NotEqual(l2, l3);            // mỗi lượt một hạt giống riêng
    }

    [Fact]
    public void Selector_ThiHetBo_MoiLuotVanCungCacCauDo()
    {
        var l1 = QuestionPoolSelector.Select(Pool1(), null, PoolCampaign, PoolCandidate, attemptNo: 1).Select(q => q.Id);
        var l2 = QuestionPoolSelector.Select(Pool1(), null, PoolCampaign, PoolCandidate, attemptNo: 2).Select(q => q.Id);

        Assert.Equal(Pool1().Select(q => q.Id), l1);
        Assert.Equal(l1, l2);
    }

    // ── Start ────────────────────────────────────────────────────────────────────────────────────

    private sealed class SessionCalls
    {
        public List<IReadOnlyList<string>> Questions { get; } = new();
        public List<int?> Durations { get; } = new();
        public int Count => Questions.Count;
    }

    /// <summary>
    /// Client giả: mỗi lần gọi trả session lấy từ <paramref name="nextSession"/> (mô phỏng create-or-get
    /// của Interview); ghi lại bộ câu + durationMinutes được gửi. Kèm 1 câu có NỘI DUNG để chứng minh
    /// Start làm rỗng content chứ không phải Interview trả rỗng.
    /// </summary>
    private static Mock<ICampaignSessionClient> SessionMock(Func<Guid> nextSession, SessionCalls calls, Exception? throwIt = null)
    {
        var m = new Mock<ICampaignSessionClient>();
        m.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SessionQuestionInput>?>(),
                It.IsAny<CampaignScoringPolicyInput?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(inv =>
            {
                calls.Questions.Add((IReadOnlyList<string>)inv.Arguments[4]);
                calls.Durations.Add((int?)inv.Arguments[16]);
                if (throwIt is not null) throw throwIt;
                return Task.FromResult(new CampaignSessionResult(nextSession(), new List<SessionQuestion>
                {
                    new(Guid.Parse("99999999-0000-0000-0000-000000000001"), 1, "Nội dung câu bí mật", 120),
                    new(Guid.Parse("99999999-0000-0000-0000-000000000002"), 2, "Câu thứ hai", 90),
                }));
            }));
        return m;
    }

    private static ParticipationService NewService(CampaignDbContext db, ICampaignSessionClient session) =>
        new(db, Mock.Of<IAuthProvisionClient>(), session, NullLogger<ParticipationService>.Instance);

    /// <summary>Chiến dịch Active, 10 câu không bắt buộc, K = 3 (ngân hàng đề) hoặc null.</summary>
    private static Campaign SeedCampaign(CampaignTestDb tdb, int maxAttempts, int? k = 3, int? timeLimit = 45,
        string language = "vi", Guid? id = null)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        if (id is Guid fixedId) camp.Id = fixedId;   // hạt giống selector tất định ⇒ test không phụ thuộc may rủi
        camp.Domain = "BE";
        camp.Language = language;
        camp.MaxAttempts = maxAttempts;
        camp.TimeLimitMinutes = timeLimit;
        camp.QuestionsPerSession = k;
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 10; i++)
            camp.Questions.Add(new CampaignQuestion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId, QuestionText = $"Câu {i}",
                Source = QuestionSource.CustomHr, IsRequired = false, CreatedAt = epoch.AddSeconds(i),
            });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Communication",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    private static CampaignMembership SeedMembership(CampaignTestDb tdb, Guid campaignId, Guid candidateId,
        int attemptCount = 0, Guid? sessionId = null, InterviewProgressStatus? status = null)
    {
        var m = CampaignTestDb.NewMembership(campaignId, candidateId, sessionId: sessionId, interviewStatus: status);
        m.AttemptCount = attemptCount;
        tdb.Db.CampaignMemberships.Add(m);
        tdb.Db.SaveChanges();
        return m;
    }

    private static CampaignMembership Reload(CampaignTestDb tdb, Guid campaignId, Guid candidateId) =>
        tdb.NewContext().CampaignMemberships.AsNoTracking().Single(x => x.CampaignId == campaignId && x.CandidateId == candidateId);

    [Fact]
    public async Task Start_LanDau_AttemptCount1_AttemptNo1()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        SeedMembership(tdb, camp.Id, cand);
        var sid = Guid.NewGuid();
        var calls = new SessionCalls();

        var res = await NewService(tdb.NewContext(), SessionMock(() => sid, calls).Object)
            .StartInterviewAsync(cand, camp.Id, default);

        Assert.Equal(1, res.AttemptNo);
        var m = Reload(tdb, camp.Id, cand);
        Assert.Equal(1, m.AttemptCount);
        Assert.Equal(sid, m.SessionId);
    }

    [Fact]
    public async Task Start_VaoLaiBuoiDangDo_KhongTang_AttemptNoVan1()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        var sid = Guid.NewGuid();
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: sid, status: InterviewProgressStatus.InProgress);
        var calls = new SessionCalls();

        // Hết lượt (1/1) NHƯNG đang làm dở ⇒ không bị chặn, create-or-get trả cùng session.
        var res = await NewService(tdb.NewContext(), SessionMock(() => sid, calls).Object)
            .StartInterviewAsync(cand, camp.Id, default);

        Assert.Equal(1, res.AttemptNo);
        Assert.Equal(1, Reload(tdb, camp.Id, cand).AttemptCount);
    }

    [Fact]
    public async Task Start_HetLuot_409_KhongGoiInterview()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.Abandoned);
        var calls = new SessionCalls();
        var session = SessionMock(Guid.NewGuid, calls);

        var ex = await Assert.ThrowsAsync<AttemptLimitReachedException>(() =>
            NewService(tdb.NewContext(), session.Object).StartInterviewAsync(cand, camp.Id, default));

        Assert.Equal(1, ex.AttemptsUsed);
        Assert.Equal(1, ex.MaxAttempts);
        Assert.Equal(0, calls.Count);
        session.Verify(x => x.CreateOrGetSessionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SessionQuestionInput>?>(),
            It.IsAny<CampaignScoringPolicyInput?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(1, Reload(tdb, camp.Id, cand).AttemptCount);
    }

    // Guard lượt đứng TRƯỚC khung giờ: khung giờ đã đóng mà hết lượt ⇒ vẫn báo hết lượt (không phải
    // "ngoài khung giờ"), chứng minh thứ tự — guard đặt sau là cửa cho các bước tốn kém chạy trước.
    [Fact]
    public async Task Start_HetLuot_DungTruocKhungGio()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        var slot = new CampaignSlot
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, StartsAt = DateTime.UtcNow.AddDays(1),
            EndsAt = DateTime.UtcNow.AddDays(1).AddHours(1), Capacity = 5,
        };
        tdb.Db.CampaignSlots.Add(slot);
        var m = SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.Abandoned);
        m.SlotId = slot.Id;
        tdb.Db.SaveChanges();

        await Assert.ThrowsAsync<AttemptLimitReachedException>(() =>
            NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls()).Object)
                .StartInterviewAsync(cand, camp.Id, default));
    }

    [Fact]
    public async Task Start_Max2_Luot2_TaoBuoiMoi_Dem2_DeKhacLuot1()
    {
        using var tdb = new CampaignTestDb();
        var cand = PoolCandidate;
        var camp = SeedCampaign(tdb, maxAttempts: 2, k: 3, id: PoolCampaign);
        SeedMembership(tdb, camp.Id, cand);
        var calls = new SessionCalls();
        var sessions = new Queue<Guid>(new[] { Guid.NewGuid(), Guid.NewGuid() });
        var mock = SessionMock(() => sessions.Dequeue(), calls);

        var r1 = await NewService(tdb.NewContext(), mock.Object).StartInterviewAsync(cand, camp.Id, default);
        Assert.Equal(1, r1.AttemptNo);

        // Lượt 1 bị bỏ ngang (RankingEventHandler đặt Abandoned, giữ SessionId của lượt 1).
        using (var ctx = tdb.NewContext())
        {
            var m = ctx.CampaignMemberships.Single(x => x.CampaignId == camp.Id && x.CandidateId == cand);
            m.InterviewStatus = InterviewProgressStatus.Abandoned;
            ctx.SaveChanges();
        }

        var r2 = await NewService(tdb.NewContext(), mock.Object).StartInterviewAsync(cand, camp.Id, default);

        Assert.Equal(2, r2.AttemptNo);
        Assert.NotEqual(r1.SessionId, r2.SessionId);
        var after = Reload(tdb, camp.Id, cand);
        Assert.Equal(2, after.AttemptCount);
        Assert.Equal(r2.SessionId, after.SessionId);
        Assert.Equal(InterviewProgressStatus.InProgress, after.InterviewStatus);
        Assert.Equal(3, calls.Questions[1].Count);
        Assert.NotEqual(calls.Questions[0], calls.Questions[1]);   // pool 10, K = 3 ⇒ đề lượt 2 khác lượt 1
    }

    // Không phải vào lại (Abandoned) nhưng Interview trả về CÙNG session đang giữ ⇒ không có buổi mới
    // ⇒ không đếm lượt.
    [Fact]
    public async Task Start_InterviewTraCungSession_KhongTang()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        var sid = Guid.NewGuid();
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: sid, status: InterviewProgressStatus.Abandoned);

        var res = await NewService(tdb.NewContext(), SessionMock(() => sid, new SessionCalls()).Object)
            .StartInterviewAsync(cand, camp.Id, default);

        Assert.Equal(1, Reload(tdb, camp.Id, cand).AttemptCount);
        Assert.Equal(1, res.AttemptNo);
    }

    public static IEnumerable<object[]> InterviewFailures() => new[]
    {
        new object[] { new DownstreamServiceException("502 giả") },
        new object[] { new InsufficientOrgCreditException("402 giả") },
        new object[] { new CampaignInterviewCapacityExceededException("429 giả") },
    };

    [Theory]
    [MemberData(nameof(InterviewFailures))]
    public async Task Start_InterviewNem_KhongMatLuot(Exception failure)
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        SeedMembership(tdb, camp.Id, cand);

        await Assert.ThrowsAsync(failure.GetType(), () =>
            NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls(), failure).Object)
                .StartInterviewAsync(cand, camp.Id, default));

        var m = Reload(tdb, camp.Id, cand);
        Assert.Equal(0, m.AttemptCount);
        Assert.Null(m.SessionId);
    }

    // ── Controller: JSON thật ────────────────────────────────────────────────────────────────────

    private static ParticipationController Controller(ParticipationService svc, Guid candidateId) => new(svc, NullLogger<ParticipationController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, candidateId.ToString()),
                    new Claim(ClaimTypes.Role, "Candidate"),
                }, "Test")),
            },
        },
    };

    private static JsonElement Json(IActionResult r)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(r);
        return JsonSerializer.SerializeToElement(obj.Value, obj.Value!.GetType(), Web);
    }

    [Fact]
    public async Task Controller_HetLuot_409_BodyCoCodeVaSoLuot()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        SeedMembership(tdb, camp.Id, cand, attemptCount: 2, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.Abandoned);

        var r = await Controller(NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls()).Object), cand)
            .StartInterview(camp.Id, default);

        Assert.IsType<ConflictObjectResult>(r);
        var body = Json(r);
        Assert.Equal("ATTEMPT_LIMIT_REACHED", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        Assert.Equal(2, body.GetProperty("attemptsUsed").GetInt32());
        Assert.Equal(2, body.GetProperty("maxAttempts").GetInt32());
    }

    [Fact]
    public async Task Controller_Start_ContentRong_CoAttemptNoVaTimeLimit_GiuIdOrderNoTimeLimitSec()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1, timeLimit: 45);
        SeedMembership(tdb, camp.Id, cand);

        var r = await Controller(NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls()).Object), cand)
            .StartInterview(camp.Id, default);

        var body = Json(Assert.IsType<OkObjectResult>(r));
        Assert.Equal(1, body.GetProperty("attemptNo").GetInt32());
        Assert.Equal(45, body.GetProperty("timeLimitMinutes").GetInt32());
        var qs = body.GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(2, qs.Count);
        Assert.All(qs, q => Assert.Equal(string.Empty, q.GetProperty("content").GetString()));
        Assert.Equal(Guid.Parse("99999999-0000-0000-0000-000000000001"), qs[0].GetProperty("id").GetGuid());
        Assert.Equal(1, qs[0].GetProperty("orderNo").GetInt32());
        Assert.Equal(120, qs[0].GetProperty("timeLimitSec").GetInt32());
        Assert.Equal(90, qs[1].GetProperty("timeLimitSec").GetInt32());
    }

    [Fact]
    public async Task Controller_MyCampaigns_VaChiTiet_Co4FieldMoi_InterviewStatusNhuCu()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 3, timeLimit: 30);
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.Abandoned);
        var controller = Controller(NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls()).Object), cand);

        var list = Json(await controller.GetMyCampaigns(null, null, default));
        var item = list.EnumerateArray().Single();
        var detail = Json(await controller.GetMyCampaign(camp.Id, default));

        foreach (var e in new[] { item, detail })
        {
            Assert.Equal(30, e.GetProperty("timeLimitMinutes").GetInt32());
            Assert.Equal(3, e.GetProperty("maxAttempts").GetInt32());
            Assert.Equal(1, e.GetProperty("attemptsUsed").GetInt32());
            Assert.True(e.GetProperty("lastAttemptAbandoned").GetBoolean());
            Assert.Equal("NotStarted", e.GetProperty("interviewStatus").GetString());   // giữ nguyên ánh xạ cũ
        }
    }

    [Fact]
    public async Task MyCampaigns_KhongBoNgang_LastAttemptAbandonedFalse()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1, timeLimit: null);
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.InProgress);

        var page = await NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls()).Object)
            .GetMyCampaignsAsync(cand, null, null, default);

        var item = page.Items.Single();
        Assert.False(item.LastAttemptAbandoned);
        Assert.Null(item.TimeLimitMinutes);
        Assert.Equal("InProgress", item.InterviewStatus);
    }

    // ── Dây sang Interview: body JSON THẬT, cả hai overload (vi / en) ────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            var json = $$"""{"id":"{{Guid.NewGuid()}}","questions":[{"id":"{{Guid.NewGuid()}}","orderNo":1,"content":"Câu bí mật","timeLimitSec":120}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static CampaignSessionClient RealClient(CapturingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://interview.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return new CampaignSessionClient(http, config, NullLogger<CampaignSessionClient>.Instance);
    }

    [Theory]
    [InlineData("vi", 45)]
    [InlineData("en", 45)]
    [InlineData("vi", null)]
    [InlineData("en", null)]
    public async Task Start_BodyGuiInterview_CoKhoaDurationMinutes_CaHaiOverload(string language, int? minutes)
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1, timeLimit: minutes, language: language);
        SeedMembership(tdb, camp.Id, cand);
        var handler = new CapturingHandler();

        var res = await NewService(tdb.NewContext(), RealClient(handler)).StartInterviewAsync(cand, camp.Id, default);

        var body = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        Assert.Equal(language, body.GetProperty("language").GetString());   // đúng overload được dùng
        Assert.True(body.TryGetProperty("durationMinutes", out var d), "thiếu khoá camelCase durationMinutes");
        if (minutes is int mm)
            Assert.Equal(mm, d.GetInt32());
        else
            Assert.Equal(JsonValueKind.Null, d.ValueKind);   // null GHI TƯỜNG MINH = không tính giờ, không bịa số
        Assert.False(body.TryGetProperty("DurationMinutes", out _));
        // Nội dung câu thật từ Interview KHÔNG ra khỏi Start.
        Assert.All(res.Questions, q => Assert.Equal(string.Empty, q.Content));
    }

    // ══ fix(att1-b2) — mục KIỂM B2 ══════════════════════════════════════════════════════════════

    // ── A1 (K6/K7): thời lượng null là JSON null ở response Start VÀ chi tiết my-campaigns ─────────
    [Fact]
    public async Task Controller_TimeLimitNull_LaJsonNull_StartVaChiTietVaDanhSach()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1, timeLimit: null);
        SeedMembership(tdb, camp.Id, cand);
        var controller = Controller(NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls()).Object), cand);

        var start = Json(Assert.IsType<OkObjectResult>(await controller.StartInterview(camp.Id, default)));
        var detail = Json(await controller.GetMyCampaign(camp.Id, default));
        var item = Json(await controller.GetMyCampaigns(null, null, default)).EnumerateArray().Single();

        Assert.Equal(JsonValueKind.Null, start.GetProperty("timeLimitMinutes").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("timeLimitMinutes").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("timeLimitMinutes").ValueKind);
    }

    // ── A2 (K12/K4): fixture giá trị KHÁC NHAU — maxAttempts 2 ≠ K 3; attempt_count 3 > max 2 ──────
    [Fact]
    public async Task Controller_MaxAttemptsKhacK_DanhSachVaChiTietDungNguon()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2, k: 3);
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.Abandoned);
        var controller = Controller(NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls()).Object), cand);

        var item = Json(await controller.GetMyCampaigns(null, null, default)).EnumerateArray().Single();
        var detail = Json(await controller.GetMyCampaign(camp.Id, default));

        Assert.Equal(2, item.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(2, detail.GetProperty("maxAttempts").GetInt32());
    }

    [Fact]
    public async Task Controller_HetLuot_AttemptsUsedVuotMax_BaoDungTungNguon()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        SeedMembership(tdb, camp.Id, cand, attemptCount: 3, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.Abandoned);

        var r = await Controller(NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls()).Object), cand)
            .StartInterview(camp.Id, default);

        var body = Json(Assert.IsType<ConflictObjectResult>(r));
        Assert.Equal("ATTEMPT_LIMIT_REACHED", body.GetProperty("code").GetString());
        Assert.Equal(3, body.GetProperty("attemptsUsed").GetInt32());
        Assert.Equal(2, body.GetProperty("maxAttempts").GetInt32());
    }

    // ── A3 (K1/K2): khoá danh sách id lượt 2 và lượt 3 (đo trên selector hiện tại, khớp số người kiểm đo).
    //    Đổi endianness 4 byte lượt hay để 4 byte = 0 ở lượt 2 là đổi đề của MỌI người đang làm lượt 2.
    [Fact]
    public void Selector_Luot2VaLuot3_KhoaDanhSachId()
    {
        Assert.Equal(new[] { G(34), G(31), G(37) },
            QuestionPoolSelector.Select(Plain10(), 3, PoolCampaign, PoolCandidate, attemptNo: 2).Select(q => q.Id));
        Assert.Equal(new[] { G(36), G(39), G(40) },
            QuestionPoolSelector.Select(Plain10(), 3, PoolCampaign, PoolCandidate, attemptNo: 3).Select(q => q.Id));
    }

    // ── B (R1): membership InProgress nhưng Interview đã đóng buổi (sự kiện chưa tới) ─────────────

    private static Mock<ICampaignSessionClient> WithState(Mock<ICampaignSessionClient> m, CampaignSessionState state)
    {
        m.Setup(x => x.GetSessionStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(state);
        return m;
    }

    private static void VerifyCreateOrGet(Mock<ICampaignSessionClient> m, Times times) =>
        m.Verify(x => x.CreateOrGetSessionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
            It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SessionQuestionInput>?>(),
            It.IsAny<CampaignScoringPolicyInput?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            times);

    public static IEnumerable<object[]> EndedStates() => new[]
    {
        new object[] { true, "SessionAbandoned" },
        new object[] { true, "Failed" },
        new object[] { false, null! },   // không có trong existingIds ⇒ coi như không vào lại
    };

    [Theory]
    [MemberData(nameof(EndedStates))]
    public async Task Start_TuongVaoLai_NhungBuoiDaDong_HetLuot_409_KhongGoiCreateOrGet(bool exists, string? status)
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        var s1 = Guid.NewGuid();
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: s1, status: InterviewProgressStatus.InProgress);
        var mock = WithState(SessionMock(Guid.NewGuid, new SessionCalls()), new CampaignSessionState(exists, status));

        var ex = await Assert.ThrowsAsync<AttemptLimitReachedException>(() =>
            NewService(tdb.NewContext(), mock.Object).StartInterviewAsync(cand, camp.Id, default));

        Assert.Equal(1, ex.AttemptsUsed);
        VerifyCreateOrGet(mock, Times.Never());
        mock.Verify(x => x.GetSessionStateAsync(s1, It.IsAny<CancellationToken>()), Times.Once);
        var after = Reload(tdb, camp.Id, cand);
        Assert.Equal(1, after.AttemptCount);
        Assert.Equal(s1, after.SessionId);
    }

    [Fact]
    public async Task Start_TuongVaoLai_NhungBuoiDaBoNgang_ConLuot_LuotMoi_DeLuot2()
    {
        using var tdb = new CampaignTestDb();
        var cand = PoolCandidate;
        var camp = SeedCampaign(tdb, maxAttempts: 2, k: 3, id: PoolCampaign);
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: s1, status: InterviewProgressStatus.InProgress);
        var calls = new SessionCalls();
        var mock = WithState(SessionMock(() => s2, calls), new CampaignSessionState(true, "SessionAbandoned"));

        var res = await NewService(tdb.NewContext(), mock.Object).StartInterviewAsync(cand, camp.Id, default);

        Assert.Equal(2, res.AttemptNo);
        var after = Reload(tdb, camp.Id, cand);
        Assert.Equal(2, after.AttemptCount);
        Assert.Equal(s2, after.SessionId);
        var pool = tdb.NewContext().CampaignQuestions.AsNoTracking().Where(q => q.CampaignId == camp.Id)
            .OrderBy(q => q.CreatedAt).ThenBy(q => q.Id)
            .Select(q => new PoolQuestion(q.Id, q.QuestionText, q.SampleAnswer, q.IsRequired, q.QuestionGroup)).ToList();
        var l1 = QuestionPoolSelector.Select(pool, 3, camp.Id, cand, attemptNo: 1).Select(q => q.Text);
        var l2 = QuestionPoolSelector.Select(pool, 3, camp.Id, cand, attemptNo: 2).Select(q => q.Text);
        Assert.Equal(l2, calls.Questions.Single());   // lượt MỚI ⇒ đề lượt 2
        Assert.NotEqual(l1, calls.Questions.Single());
    }

    // ── Mốc lượt (attempt_started_at): đặt ĐÚNG khi Start tạo buổi mới, cùng vị ngữ với AttemptCount ──

    private static readonly DateTime MocLuot1 = new(2026, 10, 3, 15, 52, 12, DateTimeKind.Utc);

    private static void SetMoc(CampaignTestDb tdb, CampaignMembership m, DateTime? first, DateTime? attempt)
    {
        m.InterviewStartedAt = first;
        m.AttemptStartedAt = attempt;
        tdb.Db.SaveChanges();
    }

    // R1: membership còn InProgress nhưng Interview báo buổi cũ đã bỏ ngang ⇒ lượt MỚI. Ca này KHÔNG đi
    // vào khối chuyển-trạng-thái (đã InProgress) ⇒ mốc lượt phải đặt ở khối isNewSession, nếu không lượt 2
    // mang mốc lượt 1. Mốc lần đầu giữ nguyên.
    [Fact]
    public async Task Start_R1_BuoiCuDaBoNgang_DatMocLuotMoi_GiuMocLanDau()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var m = SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: s1, status: InterviewProgressStatus.InProgress);
        SetMoc(tdb, m, MocLuot1, MocLuot1);
        var mock = WithState(SessionMock(() => s2, new SessionCalls()), new CampaignSessionState(true, "SessionAbandoned"));

        var before = DateTime.UtcNow;
        await NewService(tdb.NewContext(), mock.Object).StartInterviewAsync(cand, camp.Id, default);
        var after = DateTime.UtcNow;

        var r = Reload(tdb, camp.Id, cand);
        Assert.Equal(s2, r.SessionId);
        Assert.Equal(MocLuot1, r.InterviewStartedAt);
        Assert.NotNull(r.AttemptStartedAt);
        Assert.InRange(r.AttemptStartedAt!.Value, before, after);
    }

    // Abandoned nhưng Interview trả LẠI buổi đang giữ ⇒ không có buổi mới ⇒ không đếm lượt, không dời mốc.
    [Fact]
    public async Task Start_InterviewTraCungSession_KhongDoiMocLuot()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        var sid = Guid.NewGuid();
        var m = SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: sid, status: InterviewProgressStatus.Abandoned);
        SetMoc(tdb, m, MocLuot1, MocLuot1);

        await NewService(tdb.NewContext(), SessionMock(() => sid, new SessionCalls()).Object)
            .StartInterviewAsync(cand, camp.Id, default);

        var r = Reload(tdb, camp.Id, cand);
        Assert.Equal(MocLuot1, r.AttemptStartedAt);
        Assert.Equal(MocLuot1, r.InterviewStartedAt);
    }

    // Interview ném (402/429/502) ⇒ chưa có buổi nào ⇒ mốc lượt giữ nguyên (cùng SaveChanges với SessionId).
    [Theory]
    [MemberData(nameof(InterviewFailures))]
    public async Task Start_InterviewNem_KhongDoiMocLuot(Exception failure)
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        var s1 = Guid.NewGuid();
        var m = SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: s1, status: InterviewProgressStatus.Abandoned);
        SetMoc(tdb, m, MocLuot1, MocLuot1);

        await Assert.ThrowsAsync(failure.GetType(), () =>
            NewService(tdb.NewContext(), SessionMock(Guid.NewGuid, new SessionCalls(), failure).Object)
                .StartInterviewAsync(cand, camp.Id, default));

        var r = Reload(tdb, camp.Id, cand);
        Assert.Equal(s1, r.SessionId);
        Assert.Equal(MocLuot1, r.AttemptStartedAt);
    }

    [Theory]
    [InlineData("Scored")]
    [InlineData("Completed")]
    public async Task Start_TuongVaoLai_NhungBuoiDaNop_409KhongCode_KhongGoiCreateOrGet(string status)
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);   // còn lượt nhưng bài ĐÃ NỘP ⇒ không cho làm lại
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.InProgress);
        var mock = WithState(SessionMock(Guid.NewGuid, new SessionCalls()), new CampaignSessionState(true, status));

        var r = await Controller(NewService(tdb.NewContext(), mock.Object), cand).StartInterview(camp.Id, default);

        var body = Json(Assert.IsType<ConflictObjectResult>(r));
        Assert.False(body.TryGetProperty("code", out _));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        VerifyCreateOrGet(mock, Times.Never());
        Assert.Equal(1, Reload(tdb, camp.Id, cand).AttemptCount);
    }

    [Theory]
    [InlineData("InProgress")]
    [InlineData("Ready")]
    [InlineData("GeneratingQuestions")]
    [InlineData("Scoring")]
    public async Task Start_BuoiVanDangChay_VaoLai_KhongTang(string status)
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        var s1 = Guid.NewGuid();
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: s1, status: InterviewProgressStatus.InProgress);
        var mock = WithState(SessionMock(() => s1, new SessionCalls()), new CampaignSessionState(true, status));

        var res = await NewService(tdb.NewContext(), mock.Object).StartInterviewAsync(cand, camp.Id, default);

        Assert.Equal(s1, res.SessionId);
        Assert.Equal(1, res.AttemptNo);
        Assert.Equal(1, Reload(tdb, camp.Id, cand).AttemptCount);
        VerifyCreateOrGet(mock, Times.Once());
    }

    [Fact]
    public async Task Start_HoiTrangThaiLoi_502_FailClosed_KhongGoiCreateOrGet()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.InProgress);
        var mock = SessionMock(Guid.NewGuid, new SessionCalls());
        mock.Setup(x => x.GetSessionStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamServiceException("Interview chết"));

        var r = await Controller(NewService(tdb.NewContext(), mock.Object), cand).StartInterview(camp.Id, default);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsAssignableFrom<ObjectResult>(r).StatusCode);
        VerifyCreateOrGet(mock, Times.Never());
        Assert.Equal(1, Reload(tdb, camp.Id, cand).AttemptCount);
    }

    // CHỈ hỏi trạng thái khi membership tưởng đang làm dở: Start lần đầu và membership Abandoned không thêm
    // round-trip.
    [Theory]
    [InlineData(false)]   // lần đầu: SessionId null
    [InlineData(true)]    // lượt trước bỏ ngang: SessionId có, Abandoned
    public async Task Start_KhongPhaiVaoLai_KhongHoiTrangThai(bool abandoned)
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        if (abandoned)
            SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: Guid.NewGuid(), status: InterviewProgressStatus.Abandoned);
        else
            SeedMembership(tdb, camp.Id, cand);
        var mock = WithState(SessionMock(Guid.NewGuid, new SessionCalls()), new CampaignSessionState(true, "InProgress"));

        await NewService(tdb.NewContext(), mock.Object).StartInterviewAsync(cand, camp.Id, default);

        mock.Verify(x => x.GetSessionStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── B: client thật — body JSON thật + phân tích phản hồi + fail-closed ───────────────────────

    private sealed class ExistsHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _json;
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? Token { get; private set; }

        public ExistsHandler(HttpStatusCode code, string json) { _code = code; _json = json; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Token = request.Headers.TryGetValues("X-Internal-Token", out var v) ? v.Single() : null;
            return new HttpResponseMessage(_code) { Content = new StringContent(_json, Encoding.UTF8, "application/json") };
        }
    }

    private static CampaignSessionClient ExistsClient(ExistsHandler h)
    {
        var http = new HttpClient(h) { BaseAddress = new Uri("http://interview.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" }).Build();
        return new CampaignSessionClient(http, config, NullLogger<CampaignSessionClient>.Instance);
    }

    [Fact]
    public async Task Client_GetSessionState_BodyCoSessionIds_DocExistsVaStatus()
    {
        var sid = Guid.NewGuid();
        var h = new ExistsHandler(HttpStatusCode.OK,
            $$"""{"existingIds":["{{sid}}"],"states":[{"sessionId":"{{sid}}","status":"SessionAbandoned"}]}""");

        var state = await ExistsClient(h).GetSessionStateAsync(sid);

        Assert.Equal("/internal/sessions/exists", h.Path);
        Assert.Equal("tkn", h.Token);
        var body = JsonDocument.Parse(h.Body!).RootElement;
        Assert.Equal(sid, body.GetProperty("sessionIds").EnumerateArray().Single().GetGuid());
        Assert.True(state.Exists);
        Assert.Equal("SessionAbandoned", state.Status);
    }

    [Fact]
    public async Task Client_GetSessionState_KhongTrongExistingIds_ExistsFalse()
    {
        var h = new ExistsHandler(HttpStatusCode.OK, """{"existingIds":[],"states":[]}""");

        var state = await ExistsClient(h).GetSessionStateAsync(Guid.NewGuid());

        Assert.False(state.Exists);
        Assert.Null(state.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"x"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"Invalid internal token"}""")]
    [InlineData(HttpStatusCode.OK, "khong-phai-json")]
    [InlineData(HttpStatusCode.OK, """{"states":[]}""")]   // thiếu existingIds ⇒ không đoán "không tồn tại"
    public async Task Client_GetSessionState_LoiHoacPhanHoiHong_FailClosed(HttpStatusCode code, string json)
    {
        var h = new ExistsHandler(code, json);

        await Assert.ThrowsAsync<DownstreamServiceException>(() => ExistsClient(h).GetSessionStateAsync(Guid.NewGuid()));
    }

    // ── C (R2): SessionScored của lượt CŨ tới muộn khi membership đã sang lượt mới ───────────────
    [Fact]
    public async Task Scored_CuaSessionCu_KhiMembershipDaTroSessionMoi_KhongDanhCompleted_VanUpsertRanking()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var m = SeedMembership(tdb, camp.Id, cand, attemptCount: 2, sessionId: s2, status: InterviewProgressStatus.InProgress);
        m.InterviewDeadlineAt = DateTime.UtcNow.AddDays(1);
        tdb.Db.SaveChanges();

        await new RankingEventHandler(tdb.NewContext(), NullLogger<RankingEventHandler>.Instance)
            .HandleSessionScoredAsync(new SessionScoredMessage
            {
                SessionId = s1, CampaignId = camp.Id, CandidateId = cand, TotalScore = 40m, ScoredAt = DateTime.UtcNow,
            });

        var after = Reload(tdb, camp.Id, cand);
        Assert.Equal(InterviewProgressStatus.InProgress, after.InterviewStatus);
        Assert.Equal(s2, after.SessionId);
        // Đường ranking GIỮ NGUYÊN: buổi s1 đã chấm vẫn có dòng xếp hạng của nó.
        Assert.Contains(tdb.NewContext().CampaignRankings.AsNoTracking(), r => r.SessionId == s1);
    }

    [Fact]
    public async Task Scored_CuaChinhSessionDangGiu_VanDanhCompleted()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        var s1 = Guid.NewGuid();
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: s1, status: InterviewProgressStatus.InProgress);

        await new RankingEventHandler(tdb.NewContext(), NullLogger<RankingEventHandler>.Instance)
            .HandleSessionScoredAsync(new SessionScoredMessage
            {
                SessionId = s1, CampaignId = camp.Id, CandidateId = cand, TotalScore = 40m, ScoredAt = DateTime.UtcNow,
            });

        Assert.Equal(InterviewProgressStatus.Completed, Reload(tdb, camp.Id, cand).InterviewStatus);
    }

    // ── D (R3): buổi bắt đầu trong cửa sổ "đã migrate, chưa deploy code" mang attempt_count 0 ─────
    [Fact]
    public async Task Start_VaoLai_AttemptCount0_ResponseAttemptNoToiThieu1()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        var s1 = Guid.NewGuid();
        SeedMembership(tdb, camp.Id, cand, attemptCount: 0, sessionId: s1, status: InterviewProgressStatus.InProgress);
        var mock = WithState(SessionMock(() => s1, new SessionCalls()), new CampaignSessionState(true, "InProgress"));

        var res = await NewService(tdb.NewContext(), mock.Object).StartInterviewAsync(cand, camp.Id, default);

        Assert.Equal(1, res.AttemptNo);
        Assert.Equal(0, Reload(tdb, camp.Id, cand).AttemptCount);   // dữ liệu xử lý bằng backfill, không ở đây
    }

    // ── CHECK DB: attempt_count >= 0 (SQLite CÓ enforce CHECK khai ở model) ─────────────────────

    [Fact]
    public void Db_CheckAttemptCountAm_BiChan()
    {
        using var tdb = new CampaignTestDb();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        var m = CampaignTestDb.NewMembership(camp.Id, Guid.NewGuid());
        m.AttemptCount = -1;
        tdb.Db.CampaignMemberships.Add(m);

        Assert.Throws<DbUpdateException>(() => tdb.Db.SaveChanges());
    }

    // ── RankingEventHandler: sự kiện bỏ ngang của lượt CŨ ───────────────────────────────────────

    [Fact]
    public async Task Abandoned_CuaSessionCu_KhiMembershipDaTroSessionMoi_KhongDoiTrangThai()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        var oldSid = Guid.NewGuid();
        var newSid = Guid.NewGuid();
        var deadline = DateTime.UtcNow.AddDays(1);
        var m = SeedMembership(tdb, camp.Id, cand, attemptCount: 2, sessionId: newSid, status: InterviewProgressStatus.InProgress);
        m.InterviewDeadlineAt = deadline;
        tdb.Db.SaveChanges();

        await new RankingEventHandler(tdb.NewContext(), NullLogger<RankingEventHandler>.Instance)
            .HandleSessionAbandonedAsync(new SessionAbandonedMessage
            {
                SessionId = oldSid, CampaignId = camp.Id, CandidateId = cand, Reason = "expired_no_answer",
                AbandonedAt = DateTime.UtcNow,
            });

        var after = Reload(tdb, camp.Id, cand);
        Assert.Equal(InterviewProgressStatus.InProgress, after.InterviewStatus);
        Assert.Equal(newSid, after.SessionId);
        Assert.NotNull(after.InterviewDeadlineAt);
        Assert.Equal(2, after.AttemptCount);
    }

    [Fact]
    public async Task Abandoned_MembershipSessionNull_FallbackCampaignCandidate_VanDatAbandoned()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 1);
        SeedMembership(tdb, camp.Id, cand, status: InterviewProgressStatus.InProgress);
        var sid = Guid.NewGuid();

        await new RankingEventHandler(tdb.NewContext(), NullLogger<RankingEventHandler>.Instance)
            .HandleSessionAbandonedAsync(new SessionAbandonedMessage
            {
                SessionId = sid, CampaignId = camp.Id, CandidateId = cand, Reason = "expired_no_answer",
                AbandonedAt = DateTime.UtcNow,
            });

        var after = Reload(tdb, camp.Id, cand);
        Assert.Equal(InterviewProgressStatus.Abandoned, after.InterviewStatus);
        Assert.Equal(sid, after.SessionId);
    }

    [Fact]
    public async Task Abandoned_CuaChinhSessionDangGiu_VanDatAbandoned()
    {
        using var tdb = new CampaignTestDb();
        var cand = Guid.NewGuid();
        var camp = SeedCampaign(tdb, maxAttempts: 2);
        var sid = Guid.NewGuid();
        SeedMembership(tdb, camp.Id, cand, attemptCount: 1, sessionId: sid, status: InterviewProgressStatus.InProgress);

        await new RankingEventHandler(tdb.NewContext(), NullLogger<RankingEventHandler>.Instance)
            .HandleSessionAbandonedAsync(new SessionAbandonedMessage
            {
                SessionId = sid, CampaignId = camp.Id, CandidateId = cand, Reason = "expired_no_answer",
                AbandonedAt = DateTime.UtcNow,
            });

        Assert.Equal(InterviewProgressStatus.Abandoned, Reload(tdb, camp.Id, cand).InterviewStatus);
    }
}
