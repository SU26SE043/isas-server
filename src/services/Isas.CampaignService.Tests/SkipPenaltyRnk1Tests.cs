using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Isas.CampaignService.Tests;

/// <summary>
/// RNK1 · HĐ-1 / HĐ-2 / CAMP-21 — luật "câu HR khai mà ứng viên bỏ trống tính 0 điểm" phía Campaign.
///
/// <list type="bullet">
///   <item><b>Server-owned</b>: <c>skip_penalty</c> KHÔNG có trên Create/Update request.</item>
///   <item><b>Dây</b>: <c>CampaignSessionClient</c> gửi khoá camelCase <c>skipPenalty</c>;
///     <c>ParticipationService</c> truyền <c>campaign.SkipPenalty</c>.</item>
///   <item><b>Parity</b>: <c>ScoringPolicyService</c> (preview/áp) nhân <c>seed_completeness</c> qua
///     CÙNG <see cref="SkipPenaltyRule.Apply"/> mà đường chấm thường dùng; snapshot trước RNK1
///     (thiếu <c>seedTotal</c>) ⇒ không đổi điểm.</item>
/// </list>
/// </summary>
public class SkipPenaltyRnk1Tests
{
    // ── HĐ-2: server-owned — không nhận từ POST/PUT ─────────────────────────────────────────────
    [Fact]
    public void CreateUpdateRequest_KhongCoTruongSkipPenalty()
    {
        Assert.Null(typeof(CreateCampaignRequest).GetProperty("SkipPenalty",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
        Assert.Null(typeof(UpdateCampaignRequest).GetProperty("SkipPenalty",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
        // Nhưng CÓ trên response (để FE hiển thị).
        Assert.NotNull(typeof(CampaignResponse).GetProperty("SkipPenalty"));
    }

    [Fact]
    public void Response_FromEntity_MangSkipPenalty()
    {
        var c = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Draft);
        c.SkipPenalty = true;
        Assert.True(CampaignResponse.FromEntity(c).SkipPenalty);
        c.SkipPenalty = false;
        Assert.False(CampaignResponse.FromEntity(c).SkipPenalty);
    }

    // ── HĐ-1 dây: CampaignSessionClient gửi khoá camelCase "skipPenalty" = giá trị truyền vào ────
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"11111111-1111-1111-1111-111111111111","questions":[]}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private static CampaignSessionClient NewClient(CaptureHandler h)
    {
        var http = new HttpClient(h) { BaseAddress = new Uri("http://interview.test") };
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" }).Build();
        return new CampaignSessionClient(http, cfg, NullLogger<CampaignSessionClient>.Instance);
    }

    private static readonly IReadOnlyList<string> Qs = new List<string> { "Q1" };
    private static readonly IReadOnlyList<SessionCriterionInput> Crits =
        new List<SessionCriterionInput> { new("Communication", null, 1.0m, 5) };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Payload_MangKhoaSkipPenalty_CamelCase(bool skipPenalty)
    {
        var h = new CaptureHandler();
        await NewClient(h).CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE", Qs, Crits,
            skipPenalty: skipPenalty, ct: default);

        using var doc = JsonDocument.Parse(h.Body!);
        Assert.True(doc.RootElement.TryGetProperty("skipPenalty", out var v));   // literal camelCase
        Assert.Equal(skipPenalty, v.GetBoolean());
    }

    // ── HĐ-2 dây: ParticipationService.StartInterviewAsync truyền campaign.SkipPenalty ───────────
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartInterview_TruyenCampaignSkipPenalty(bool campaignSkipPenalty)
    {
        using var tdb = new CampaignTestDb();
        var candidate = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.SkipPenalty = campaignSkipPenalty;
        camp.Questions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId,
            QuestionText = "Q?", Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow,
        });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Chuyên môn",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(camp.Id, candidate));
        tdb.Db.SaveChanges();

        bool? captured = null;
        var session = new Mock<ICampaignSessionClient>();
        session.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<SessionQuestionInput>?>(), It.IsAny<CampaignScoringPolicyInput?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid _, Guid _, string _, IReadOnlyList<string> _,
                    IReadOnlyList<SessionCriterionInput> _, DateTime? _, bool? _, int? _, int? _,
                    int? _, string _, int _, IReadOnlyList<SessionQuestionInput>? _,
                    CampaignScoringPolicyInput? _, bool sp, CancellationToken _) => { captured = sp; })
            .ReturnsAsync(new CampaignSessionResult(Guid.NewGuid(), new List<SessionQuestion>()));

        var svc = new ParticipationService(
            tdb.NewContext(), Mock.Of<IAuthProvisionClient>(), session.Object,
            NullLogger<ParticipationService>.Instance);
        await svc.StartInterviewAsync(candidate, camp.Id, default);

        Assert.Equal(campaignSkipPenalty, captured);
    }

    // ── Parity: ScoringPolicyService (preview) nhân seed_completeness CÙNG hàm với đường chấm ────
    private static ScoringInputsSnapshot Bag(
        bool? skipPenalty, int? seedAnswered, int? seedTotal, decimal pctA = 80m, decimal pctB = 40m)
        => new(
            new[]
            {
                new CriterionInputSnapshot("Giao tiếp", pctA, 0.5m, 5),
                new CriterionInputSnapshot("Kỹ thuật", pctB, 0.5m, 5),
            },
            Answered: 8, TotalQuestions: 10,
            SeedAnswered: seedAnswered, SeedTotal: seedTotal, SkipPenalty: skipPenalty);

    private static async Task<decimal?> PreviewNewScore(ScoringInputsSnapshot bag, string expr = "weighted_avg_pct")
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        camp.Domain = "BE";
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, CandidateId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(), TotalScore = 60m, ScoringInputs = bag, UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.SaveChanges();

        var svc = new ScoringPolicyService(tdb.NewContext());
        var res = await svc.PreviewPolicyAsync(orgId, camp.Id,
            new ScoringPolicyPreviewRequest { Kind = "Interview", Expression = expr, PassScorePct = 60 },
            cursor: null, limit: null, default);
        return Assert.Single(res.Rows).NewScore;
    }

    [Fact]
    public async Task Preview_SkipPenaltyTrue_NhanSeedCompleteness()
    {
        var bag = Bag(skipPenalty: true, seedAnswered: 3, seedTotal: 5);   // weighted_avg_pct = 60
        var newScore = await PreviewNewScore(bag);

        Assert.Equal(36m, newScore);   // 60 × 3/5
        // Byte-equal với hàm Shared (đường chấm thường dùng cùng hàm này).
        Assert.Equal(SkipPenaltyRule.Apply(60m, bag.ToInterviewInputs()), newScore);
    }

    [Fact]
    public async Task Preview_SkipPenaltyFalse_KhongNhan()
        => Assert.Equal(60m, await PreviewNewScore(Bag(skipPenalty: false, seedAnswered: 3, seedTotal: 5)));

    [Fact]
    public async Task Preview_SnapshotTruocRnk1_SeedNull_KhongDoiDiem()
        => Assert.Equal(60m, await PreviewNewScore(Bag(skipPenalty: null, seedAnswered: null, seedTotal: null)));

    [Fact]
    public async Task Preview_SkipPenaltyTrue_SeedTotal0_KhongChia0_GiuNguyen()
        => Assert.Equal(60m, await PreviewNewScore(Bag(skipPenalty: true, seedAnswered: 0, seedTotal: 0)));
}
