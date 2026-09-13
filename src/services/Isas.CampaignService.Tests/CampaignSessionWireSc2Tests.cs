using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SC2 · W4 (T3) — DÂY Campaign → Interview <c>POST /internal/sessions/campaign</c>:
/// <c>criteria[].scoringScope</c> + <c>questionDetails[].targetCriterionIds</c>.
///
/// <para>Khoá 3 vế:</para>
/// <list type="number">
/// <item><b>I9 hợp đồng chéo</b> — tên khoá JSON phải khớp TỪNG CHỮ với property của DTO Interview
///   (<c>CampaignCriterionInput.ScoringScope</c>, <c>CampaignQuestionInput.TargetCriterionIds</c> trong
///   <c>Isas.InterviewService/DTOs/PracticeSession.cs</c>). Lệch tên KHÔNG ném lỗi: ASP.NET bind hụt ⇒ null ⇒
///   Interview chấm đủ bộ ⇒ INT-18 chết câm ở B2B (lớp bug đã cắn repo 4 lần). Test đọc THẲNG file DTO.</item>
/// <item><b>I2 trên dây</b> — <c>targetCriterionIds</c> giữ ĐÚNG 3 trạng thái: <c>null</c> ⇒ JSON <c>null</c> ·
///   <c>[]</c> ⇒ JSON <c>[]</c> · <c>[ids]</c>. <c>scoringScope</c> null ⇒ JSON null (Interview coi Always).</item>
/// <item><b>Khe nối</b> — <c>ParticipationService.StartInterviewAsync</c> gửi <c>ScoringScope</c> từ entity và
///   <c>TargetCriterionIds</c> của ĐÚNG câu đã rút (selector xáo/cắt ⇒ ghim theo Text, không theo chỉ số pool gốc).</item>
/// </list>
///
/// <para>I5: campaign không nhãn, tiêu chí mặc định ⇒ <c>targetCriterionIds: null</c>, <c>scoringScope: "Always"</c>
/// — Interview stamp <c>scoring_scope_version = 1</c>, hành vi hôm nay.</para>
/// </summary>
public class CampaignSessionWireSc2Tests
{
    // ═══════════════ hạ tầng ═══════════════

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"id\":\"{Guid.NewGuid()}\",\"questions\":[]}}", Encoding.UTF8, "application/json")
            };
        }
    }

    private static (CampaignSessionClient client, CapturingHandler handler) NewClient()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://interview.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" }).Build();
        return (new CampaignSessionClient(http, config, NullLogger<CampaignSessionClient>.Instance), handler);
    }

    private static JsonNode Sent(CapturingHandler h) => JsonNode.Parse(h.Body!)!;

    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private static string InterviewDto()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "services", "Isas.InterviewService", "DTOs", "PracticeSession.cs"));

    /// <summary>Thân một record positional trong file DTO Interview: từ <c>public record NAME(</c> tới <c>);</c>.</summary>
    private static string RecordBody(string src, string name)
    {
        var start = src.IndexOf($"public record {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"PracticeSession.cs thiếu record {name}");
        var end = src.IndexOf(");", start, StringComparison.Ordinal);
        return src[start..end];
    }

    private static string CamelCase(string pascal) => char.ToLowerInvariant(pascal[0]) + pascal[1..];

    private static CampaignCriterion Crit(string name, CriterionScoringScope scope, int order = 0)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = Guid.NewGuid(), OrderNo = order, Name = name, Weight = 0.5m, MaxScore = 5,
            Source = CriterionSource.HrEdited, ScoringScope = scope, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    private static Task Send(CampaignSessionClient client, IReadOnlyList<SessionCriterionInput> criteria,
        IReadOnlyList<SessionQuestionInput>? details, IReadOnlyList<string>? questions = null)
        => client.CreateOrGetSessionAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            questions ?? details?.Select(d => d.Text).ToList() ?? new List<string> { "Q1" },
            criteria, null, questionDetails: details, ct: default);

    // ═══════════════ (1) I9 — hợp đồng chéo đọc thẳng DTO Interview ═══════════════

    [Fact]
    public void I9_DtoInterview_CoScoringScope_VaTargetCriterionIds_DungTenProperty()
    {
        var src = InterviewDto();

        var crit = RecordBody(src, "CampaignCriterionInput");
        Assert.Matches(new Regex(@"string\?\s+ScoringScope\b"), crit);

        var q = RecordBody(src, "CampaignQuestionInput");
        Assert.Matches(new Regex(@"IReadOnlyList<Guid>\?\s+TargetCriterionIds\b"), q);

        // Khoá JSON Campaign phát = camelCase(property Interview) — đúng đường ASP.NET bind (Web defaults).
        Assert.Equal("scoringScope", CamelCase("ScoringScope"));
        Assert.Equal("targetCriterionIds", CamelCase("TargetCriterionIds"));
    }

    [Fact]
    public async Task I9_PayloadThat_MangDungKhoa_scoringScope_targetCriterionIds()
    {
        var (client, handler) = NewClient();
        var a = Guid.NewGuid();
        var criteria = ScoringCriteriaBuilder.Build(new[] { Crit("Noi dung", CriterionScoringScope.WhenTargeted) });

        await Send(client, criteria, new[] { new SessionQuestionInput("Q1", null) { TargetCriterionIds = new[] { a } } });

        var root = Sent(handler);
        var c0 = root["criteria"]!.AsArray()[0]!.AsObject();
        Assert.True(c0.ContainsKey("scoringScope"), "thiếu khoá 'scoringScope' — ASP.NET Interview bind hụt ⇒ Always im lặng");
        Assert.Equal("WhenTargeted", (string)c0["scoringScope"]!);

        var q0 = root["questionDetails"]!.AsArray()[0]!.AsObject();
        Assert.True(q0.ContainsKey("targetCriterionIds"), "thiếu khoá 'targetCriterionIds'");
        Assert.Equal(a, (Guid)q0["targetCriterionIds"]!.AsArray()[0]!);

        // Tên khoá phát ra == camelCase(tên property Interview) — hai đầu dây cùng một chữ.
        var src = InterviewDto();
        Assert.Contains(CamelCase("ScoringScope"), c0.Select(p => p.Key));
        Assert.Contains(CamelCase("TargetCriterionIds"), q0.Select(p => p.Key));
        Assert.Contains("ScoringScope", RecordBody(src, "CampaignCriterionInput"));
        Assert.Contains("TargetCriterionIds", RecordBody(src, "CampaignQuestionInput"));
    }

    /// <summary>
    /// W5 (R10c) — hợp đồng chéo đường NHẬN: Interview <c>InternalRubricsController</c> phát khoá
    /// <c>scoringScope</c> trong <c>criteria[]</c>; Campaign đọc bằng <c>B2CRubricApiCriterion.ScoringScope</c>.
    /// Đọc thẳng cả hai file: Interview đổi tên khoá ⇒ Campaign bind hụt ⇒ mọi tiêu chí chép về Always im lặng.
    /// </summary>
    [Fact]
    public void I9_W5_InterviewInternalRubrics_PhatKhoa_scoringScope_CampaignDocDungTen()
    {
        var controller = File.ReadAllText(Path.Combine(RepoRoot(), "src", "services", "Isas.InterviewService", "Controllers", "InternalRubricsController.cs"));
        Assert.Matches(new Regex(@"scoringScope\s*=\s*c\.ScoringScope\.ToString\(\)"), controller);

        var client = File.ReadAllText(Path.Combine(RepoRoot(), "src", "services", "Isas.CampaignService", "Services", "CampaignSessionClient.cs"));
        var rec = client[client.IndexOf("record B2CRubricApiCriterion(", StringComparison.Ordinal)..];
        rec = rec[..rec.IndexOf(");", StringComparison.Ordinal)];
        Assert.Matches(new Regex(@"string\?\s+ScoringScope\b"), rec);
        Assert.Equal("scoringScope", CamelCase("ScoringScope"));   // ReadFromJsonAsync Web defaults: khoá == camelCase(property)
    }

    // ═══════════════ (2) I2 — 3 trạng thái trên dây + scope từ builder ═══════════════

    [Fact]
    public async Task Wire_TargetCriterionIds_GiuDung3TrangThai_NullRongDanhSach()
    {
        var (client, handler) = NewClient();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var criteria = ScoringCriteriaBuilder.Build(new[] { Crit("A", CriterionScoringScope.Always) });

        await Send(client, criteria, new[]
        {
            new SessionQuestionInput("null-cau", null),                                          // chưa gắn
            new SessionQuestionInput("rong-cau", "mau") { TargetCriterionIds = Array.Empty<Guid>() }, // đã xét, không nhắm
            new SessionQuestionInput("nham-cau", null) { TargetCriterionIds = new[] { b, a } },
        });

        var details = Sent(handler)["questionDetails"]!.AsArray();
        Assert.Equal(3, details.Count);

        var q0 = details[0]!.AsObject();
        Assert.True(q0.ContainsKey("targetCriterionIds"), "khoá phải CÓ MẶT với giá trị null (không bỏ khoá)");
        Assert.Null(q0["targetCriterionIds"]);                              // null ⇒ null

        var q1 = details[1]!["targetCriterionIds"];
        Assert.NotNull(q1);
        Assert.Empty(q1!.AsArray());                                        // [] ⇒ [] (KHÔNG gộp về null)
        Assert.Equal("mau", (string)details[1]!["sampleAnswer"]!);         // field cũ còn nguyên

        Assert.Equal(new[] { b, a }, details[2]!["targetCriterionIds"]!.AsArray().Select(n => (Guid)n!).ToArray());   // giữ thứ tự
    }

    [Fact]
    public async Task Wire_ScoringScope_TuEntity_AlwaysVaWhenTargeted_KhongPhaiHangSo()
    {
        var (client, handler) = NewClient();
        var criteria = ScoringCriteriaBuilder.Build(new[]
        {
            Crit("Cach noi", CriterionScoringScope.Always, 0),
            Crit("Noi dung", CriterionScoringScope.WhenTargeted, 1),
        });

        await Send(client, criteria, null);

        var arr = Sent(handler)["criteria"]!.AsArray();
        Assert.Equal("Always", (string)arr[0]!["scoringScope"]!);
        Assert.Equal("WhenTargeted", (string)arr[1]!["scoringScope"]!);
    }

    /// <summary>I5 — call site cũ không set scope (fixture 4-tham-số) ⇒ null trên dây ⇒ Interview coi Always.</summary>
    [Fact]
    public async Task Wire_ScoringScopeKhongSet_GuiNull_InterviewCoiAlways()
    {
        var (client, handler) = NewClient();
        await Send(client, new[] { new SessionCriterionInput("A", null, 1.0m, 5) }, null);

        var c0 = Sent(handler)["criteria"]!.AsArray()[0]!.AsObject();
        Assert.True(c0.ContainsKey("scoringScope"));
        Assert.Null(c0["scoringScope"]);
    }

    [Fact]
    public void Builder_SetScoringScopeTuEntity()
    {
        var built = ScoringCriteriaBuilder.Build(new[]
        {
            Crit("A", CriterionScoringScope.WhenTargeted, 0), Crit("B", CriterionScoringScope.Always, 1),
        });
        Assert.Equal(new[] { "WhenTargeted", "Always" }, built.Select(c => c.ScoringScope).ToArray());
    }

    // ═══════════════ (3) khe nối — Start gửi nhãn của ĐÚNG câu đã rút + scope từ entity ═══════════════

    private static readonly DateTime SeedEpoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Mock<ICampaignSessionClient> CapturingSession(
        List<IReadOnlyList<SessionCriterionInput>> criteriaSink,
        List<(IReadOnlyList<string> Questions, IReadOnlyList<SessionQuestionInput>? Details)> sink)
    {
        var m = new Mock<ICampaignSessionClient>();
        m.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SessionQuestionInput>?>(),
                It.IsAny<CampaignScoringPolicyInput?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid _, Guid _, string _, IReadOnlyList<string> qs, IReadOnlyList<SessionCriterionInput> cr,
                    DateTime? _, bool? _, int? _, int? _, int? _, string _, int _, IReadOnlyList<SessionQuestionInput>? det,
                    CampaignScoringPolicyInput? _, bool _, CancellationToken _) => { criteriaSink.Add(cr); sink.Add((qs, det)); })
            .ReturnsAsync(() => new CampaignSessionResult(Guid.NewGuid(), new List<SessionQuestion> { new(Guid.NewGuid(), 1, "Q", 120) }));
        return m;
    }

    /// <summary>
    /// 2 tiêu chí (Always + WhenTargeted) · 4 câu (2 nhãn A, 1 nhãn [], 1 null) · K=2 ⇒ selector rút + xáo.
    /// Với MỖI câu gửi đi: <c>questionDetails[i].TargetCriterionIds</c> == nhãn của câu có ĐÚNG Text đó trong DB
    /// (không phải nhãn của pool[i]); <c>questions[i]</c> == <c>questionDetails[i].Text</c>; scope đúng entity.
    /// Chạy nhiều ứng viên để phép khẳng định không phụ thuộc một hạt giống.
    /// </summary>
    [Fact]
    public async Task Start_GuiScopeTuEntity_VaNhanCuaDungCauDaRut()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.QuestionsPerSession = 2;
        var always = Guid.NewGuid(); var targeted = Guid.NewGuid();
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = always, CampaignId = camp.Id, OrderNo = 0, Name = "Cach noi", Weight = 0.5m, MaxScore = 5,
            Source = CriterionSource.HrEdited, ScoringScope = CriterionScoringScope.Always,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = targeted, CampaignId = camp.Id, OrderNo = 1, Name = "Noi dung", Weight = 0.5m, MaxScore = 5,
            Source = CriterionSource.HrEdited, ScoringScope = CriterionScoringScope.WhenTargeted,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var expected = new Dictionary<string, List<Guid>?>
        {
            ["A0"] = new() { targeted }, ["A1"] = new() { targeted }, ["E0"] = new(), ["N0"] = null,
        };
        var i = 0;
        foreach (var (text, labels) in expected)
            camp.Questions.Add(new CampaignQuestion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId, QuestionText = text,
                Source = QuestionSource.CustomHr, IsRequired = false, TargetCriterionIds = labels,
                CreatedAt = SeedEpoch.AddSeconds(i++),
            });
        tdb.Db.Campaigns.Add(camp);
        var candidates = Enumerable.Range(1, 25).Select(n => Guid.Parse($"00000000-0000-0000-0000-{n:D12}")).ToList();
        foreach (var cand in candidates) tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(camp.Id, cand));
        await tdb.Db.SaveChangesAsync();

        var criteriaSink = new List<IReadOnlyList<SessionCriterionInput>>();
        var sink = new List<(IReadOnlyList<string> Questions, IReadOnlyList<SessionQuestionInput>? Details)>();
        var session = CapturingSession(criteriaSink, sink);
        var auth = new Mock<IAuthProvisionClient>();

        foreach (var cand in candidates)
            await new ParticipationService(tdb.NewContext(), auth.Object, session.Object, NullLogger<ParticipationService>.Instance)
                .StartInterviewAsync(cand, camp.Id, default);

        Assert.Equal(25, sink.Count);
        Assert.All(criteriaSink, cr =>
        {
            var byId = cr.ToDictionary(c => c.CriterionId!.Value, c => c.ScoringScope);
            Assert.Equal("Always", byId[always]);
            Assert.Equal("WhenTargeted", byId[targeted]);
        });
        var sawShuffle = false;
        foreach (var (questions, details) in sink)
        {
            Assert.NotNull(details);
            Assert.Equal(2, details!.Count);
            Assert.Equal(questions, details.Select(d => d.Text));   // song song, cùng thứ tự
            foreach (var d in details)
            {
                var want = expected[d.Text];
                if (want is null) Assert.Null(d.TargetCriterionIds);
                else Assert.Equal(want, d.TargetCriterionIds);       // [] == [], [targeted] == [targeted]
            }
            if (details[0].Text != "A0") sawShuffle = true;
        }
        Assert.True(sawShuffle, "đối chứng: phải có ít nhất một đề không bắt đầu bằng pool[0] — nếu không, ghép theo chỉ số pool gốc cũng xanh");
    }
}
