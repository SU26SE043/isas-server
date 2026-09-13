using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SC2 · W2 — sinh câu hỏi GẮN NHÃN tiêu chí + selector rút đều theo TIÊU CHÍ CHÍNH +
/// <c>coverageWarnings</c> + <c>K_BELOW_CRITERIA_GROUPS</c>.
///
/// <para>Khoá 6 vế (AC-T2):</para>
/// <list type="number">
/// <item>DÂY: ≥1 <c>WhenTargeted</c> ⇒ payload có <c>criteria</c> CHỈ tiêu chí đó + <c>criteriaContext</c>
///   đủ bộ; 0 <c>WhenTargeted</c> ⇒ KHÔNG có khoá <c>criteria</c> (không phải <c>null</c>).</item>
/// <item>NHÃN: <c>targetCriteria</c> song song ⇒ <c>fresh[i].TargetCriterionIds</c> id hợp lệ (dedup),
///   <c>[]</c> giữ <c>[]</c>; lệch độ dài ⇒ toàn <c>null</c> + LogWarning; id lạ bị bỏ.</item>
/// <item>SELECTOR: 2 câu chính A · 2 chính B · 2 không nhãn nhóm "X", K=3 ⇒ mỗi rổ một câu; tất định
///   theo (campaignId, candidateId).</item>
/// <item>COVERAGE: liệt kê ĐÚNG tiêu chí <c>WhenTargeted</c> không câu nào nhắm; <c>Always</c> không bao giờ.</item>
/// <item>K-RULE: K=2 với 3 tiêu chí chính distinct ⇒ <c>warnings</c> có <c>K_BELOW_CRITERIA_GROUPS</c> ⇒
///   publish 400 <c>QUESTION_BANK_INVALID</c>; K=null ⇒ không; coverage-only ⇒ publish VẪN qua (D-5).</item>
/// <item>JSON: <c>questionBank.coverageWarnings</c> camelCase, LUÔN có mặt, <c>[]</c> khi sạch.</item>
/// </list>
///
/// <para>⚠ <b>I9</b> — tên khoá dây (<c>criteria</c>/<c>criterionId</c>/<c>targetCriteria</c>) là hợp đồng
/// với <c>schemas.py</c>; pydantic <c>extra='ignore'</c> nuốt khoá lạ IM LẶNG (lớp bug đã cắn repo 4 lần),
/// nên ở đây có test ĐỌC THẲNG file Python và test đút JSON THẬT vào deserializer.</para>
///
/// <para>⚠ Bẫy đã đo (CMP2/F17): <c>System.Text.Json</c> escape non-ASCII ⇒ mọi phép so trên body
/// thô dùng sentinel ASCII hoặc đi qua <c>JsonDocument</c>.</para>
/// </summary>
public class CampaignQuestionLabelsSc2Tests
{
    // ═══════════════════════════ hạ tầng ═══════════════════════════

    private static readonly Guid CampaignId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CandidateId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>ILogger ghi lại các dòng Warning — để khẳng định "lệch độ dài ⇒ LogWarning" thật.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
        public int Warnings => Entries.Count(e => e.Level == LogLevel.Warning);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        public string? Body { get; private set; }
        public CapturingHandler(string responseJson = """{"questions":["Câu 1"]}""") => _responseJson = responseJson;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (AiServiceQuestionGenerator sut, CapturingHandler handler, CapturingLogger<AiServiceQuestionGenerator> log)
        Client(string responseJson = """{"questions":["Câu 1"]}""")
    {
        var handler = new CapturingHandler(responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        var log = new CapturingLogger<AiServiceQuestionGenerator>();
        return (new AiServiceQuestionGenerator(http, config, log), handler, log);
    }

    private static JsonElement Root(CapturingHandler h) => JsonDocument.Parse(h.Body!).RootElement;

    private static QuestionCriterionContext[] Ctx(params string[] names)
        => names.Select(n => new QuestionCriterionContext(n, null)).ToArray();

    private static QuestionCriterionRef Ref(Guid id, string name, string? desc = null) => new(id, name, desc);

    /// <summary>Generator giả cho đường service: trả câu + nhãn theo chỉ số, ghi lại `criteria` đã nhận.</summary>
    private sealed class LabelingGenerator : IQuestionGenerator
    {
        private readonly List<GeneratedQuestion> _result;
        public LabelingGenerator(params GeneratedQuestion[] result) => _result = result.ToList();
        public IReadOnlyList<QuestionCriterionRef>? LastCriteria { get; private set; }
        public IReadOnlyList<QuestionCriterionContext>? LastCriteriaContext { get; private set; }

        public Task<List<string>> GenerateAsync(string jobCategory, string? jdText, int? count, CancellationToken ct = default)
            => Task.FromResult(_result.Select(r => r.Text).ToList());
        public Task<List<string>> GenerateAsync(string jobCategory, string? jdText, int? count, string seniority, CancellationToken ct)
            => GenerateAsync(jobCategory, jdText, count, ct);
        public Task<List<string>> GenerateAsync(string jobCategory, string? jdText, int? count, string seniority,
            IReadOnlyList<QuestionCriterionContext> criteriaContext, CancellationToken ct)
            => GenerateAsync(jobCategory, jdText, count, ct);
        public Task<List<GeneratedQuestion>> GenerateAsync(string jobCategory, string? jdText, int? count, string seniority,
            IReadOnlyList<QuestionCriterionContext> criteriaContext, IReadOnlyList<QuestionCriterionRef> criteria, CancellationToken ct)
        {
            LastCriteria = criteria;
            LastCriteriaContext = criteriaContext;
            return Task.FromResult(_result.ToList());
        }
    }

    private static IEntitlementClient Entitlements()
    {
        var m = new Mock<IEntitlementClient>();
        m.Setup(x => x.ResolveOrgAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignEntitlement("test", "business", 5, 10, 200, true, true, true));
        return m.Object;
    }

    private static CampaignSvc NewService(CampaignDbContext db, IQuestionGenerator? gen = null,
        ILogger<CampaignSvc>? logger = null) =>
        new(db, Mock.Of<IFileService>(), logger ?? Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>(),
            sessionClient: null, invitationOptions: null, questionGenerator: gen,
            entitlements: Entitlements());

    private static CampaignCriterion Crit(string name, CriterionScoringScope scope, int order, decimal weight)
        => new()
        {
            Id = Guid.NewGuid(), OrderNo = order, Name = name, Weight = weight, MaxScore = 5,
            Source = CriterionSource.HrEdited, ScoringScope = scope,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    private static readonly DateTime SeedEpoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CampaignQuestion Q(string text, List<Guid>? targets, bool required = false, string? group = null)
        => new()
        {
            Id = Guid.NewGuid(), QuestionText = text, Source = QuestionSource.CustomHr,
            IsRequired = required, QuestionGroup = group, TargetCriterionIds = targets,
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>Seed qua context riêng rồi dispose — service đọc qua context KHÁC (không ăn change-tracker).</summary>
    private static async Task<Campaign> SeedAsync(CampaignTestDb tdb, Guid org,
        CampaignCriterion[] criteria, CampaignQuestion[] questions,
        int? questionsPerSession = null, CampaignStatus status = CampaignStatus.Draft, string? jd = "JD: .NET")
    {
        using var db = tdb.NewContext();
        var camp = CampaignTestDb.NewCampaign(org, status);
        camp.Domain = "BE";
        camp.JDText = jd;
        camp.QuestionsPerSession = questionsPerSession;
        db.Campaigns.Add(camp);
        foreach (var c in criteria) { c.CampaignId = camp.Id; db.CampaignCriteria.Add(c); }
        var i = 0;
        foreach (var q in questions)
        {
            q.CampaignId = camp.Id; q.OrgId = org; q.CreatedAt = SeedEpoch.AddSeconds(i++);
            db.CampaignQuestions.Add(q);
        }
        await db.SaveChangesAsync();
        return camp;
    }

    private static async Task<List<CampaignQuestion>> QuestionsAsync(CampaignTestDb tdb, Guid campaignId)
    {
        using var check = tdb.NewContext();
        return await check.CampaignQuestions.AsNoTracking().Where(q => q.CampaignId == campaignId)
            .OrderBy(q => q.CreatedAt).ThenBy(q => q.Id).ToListAsync();
    }

    // Mirror của Program.cs AddJsonOptions (camelCase + JsonStringEnumConverter + Never-ignore).
    private static JsonSerializerOptions RuntimeJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new UtcDateTimeConverter());
        return o;
    }

    private static PoolQuestion Pq(string text, List<Guid>? targets = null, string? group = null, bool required = false)
        => new(Guid.NewGuid(), text, null, required, group) { TargetCriterionIds = targets };

    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    // ═══════════════════ (1) DÂY — payload `criteria` ═══════════════════

    [Fact]
    public async Task Payload_CoWhenTargeted_GuiCriteriaChiTieuChiDo_VaContextDuBo()
    {
        var (sut, handler, _) = Client();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();

        await sut.GenerateAsync("BE", "JD", 5, "Junior",
            Ctx("Giao tiep", "Thuat toan", "He thong"),          // đủ bộ (3), gồm cả Always
            new[] { Ref(a, "Thuat toan", "ZZDESC"), Ref(b, "He thong") },   // chỉ 2 WhenTargeted
            default);

        var root = Root(handler);
        var criteria = root.GetProperty("criteria").EnumerateArray().ToList();
        Assert.Equal(2, criteria.Count);
        Assert.Equal(new[] { a.ToString("D"), b.ToString("D") },
            criteria.Select(c => c.GetProperty("criterionId").GetString()).ToArray());
        Assert.Equal("Thuat toan", criteria[0].GetProperty("name").GetString());
        Assert.Equal("ZZDESC", criteria[0].GetProperty("description").GetString());
        // description vắng ⇒ null (pydantic `str | None = None` nhận), không ném, không bịa chuỗi rỗng
        Assert.Equal(JsonValueKind.Null, criteria[1].GetProperty("description").ValueKind);

        // criteriaContext vẫn ĐỦ BỘ, không bị thu hẹp theo criteria
        Assert.Equal(3, root.GetProperty("criteriaContext").GetArrayLength());
    }

    [Fact]
    public async Task Payload_KhongWhenTargeted_KHONG_CoKhoaCriteria_ContextVanCo()
    {
        var (sut, handler, _) = Client();

        await sut.GenerateAsync("BE", "JD", 5, "Junior",
            Ctx("Giao tiep", "Thuat toan"), Array.Empty<QuestionCriterionRef>(), default);

        var root = Root(handler);
        // KHÔNG có khoá — không phải `"criteria": null` (hợp đồng W2: "0 tiêu chí ⇒ không gửi khoá").
        Assert.DoesNotContain(root.EnumerateObject(), p => p.Name == "criteria");
        Assert.Equal(2, root.GetProperty("criteriaContext").GetArrayLength());
    }

    /// <summary>
    /// 🔒 I9 — tên khoá là hợp đồng với <c>schemas.py</c>. Đọc THẲNG file Python: `GenerateQuestionsRequest`
    /// phải khai <c>criteria</c>, <c>CriterionRef</c> phải có <c>criterionId</c>, response phải khai
    /// <c>targetCriteria</c>. Đổi tên một bên là pydantic nuốt im lặng — test này là thứ duy nhất kêu.
    /// </summary>
    [Fact]
    public void I9_SchemasPy_KhaiDungTenKhoa()
    {
        var path = Path.Combine(RepoRoot(), "src", "services", "Isas.AIService", "app", "schemas.py");
        Assert.True(File.Exists(path), $"Không thấy {path}");
        var py = File.ReadAllText(path);

        var reqStart = py.IndexOf("class GenerateQuestionsRequest(", StringComparison.Ordinal);
        Assert.True(reqStart >= 0, "schemas.py thiếu GenerateQuestionsRequest");
        var reqEnd = py.IndexOf("\nclass ", reqStart + 1, StringComparison.Ordinal);
        var req = py[reqStart..reqEnd];
        Assert.Contains("criteria: list[CriterionRef]", req);
        Assert.Contains("criteriaContext:", req);

        var refStart = py.IndexOf("class CriterionRef(", StringComparison.Ordinal);
        var refEnd = py.IndexOf("\nclass ", refStart + 1, StringComparison.Ordinal);
        var refBlock = py[refStart..refEnd];
        Assert.Contains("criterionId: str", refBlock);
        Assert.Contains("name: str", refBlock);

        var resStart = py.IndexOf("class GenerateQuestionsResponse(", StringComparison.Ordinal);
        var resEnd = py.IndexOf("\nclass ", resStart + 1, StringComparison.Ordinal);
        var res = py[resStart..resEnd];
        Assert.Contains("targetCriteria: list[list[str]]", res);
    }

    /// <summary>I9 vế .NET: đút JSON THẬT (đúng tên khoá Python trả) ⇒ deserializer bind được nhãn.</summary>
    [Fact]
    public async Task I9_ResponseThat_targetCriteria_BindDuocNhan()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var (sut, _, _) = Client($$"""
            {"questions":["Q1","Q2","Q3"],
             "targetCriteria":[["{{a:D}}"],[],["{{b:D}}","{{a:D}}"]]}
            """);

        var got = await sut.GenerateAsync("BE", "JD", 3, "Junior", Ctx("x"), new[] { Ref(a, "A"), Ref(b, "B") }, default);

        Assert.Equal(3, got.Count);
        Assert.Equal(new[] { a }, got[0].TargetCriterionIds);
        Assert.NotNull(got[1].TargetCriterionIds);
        Assert.Empty(got[1].TargetCriterionIds!);           // [] giữ [] (I2)
        Assert.Equal(new[] { b, a }, got[2].TargetCriterionIds);
    }

    // ═══════════════════ (2) AlignTargets ═══════════════════

    [Fact]
    public void Align_SongSong_Dedup_BoChuoiKhongPhaiGuid()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var log = new CapturingLogger<AiServiceQuestionGenerator>();

        var got = AiServiceQuestionGenerator.AlignTargets(
            new() { "Q1", "Q2" },
            new() { new() { a.ToString("D"), "not-a-guid", a.ToString("D"), b.ToString("D") }, new() { b.ToString("D") } },
            log);

        Assert.Equal(new[] { a, b }, got[0].TargetCriterionIds);
        Assert.Equal(new[] { b }, got[1].TargetCriterionIds);
        Assert.Equal(0, log.Warnings);
    }

    [Fact]
    public void Align_VangKhoa_MoiCauNull()
    {
        var got = AiServiceQuestionGenerator.AlignTargets(new() { "Q1", "Q2" }, null, NullLogger.Instance);
        Assert.All(got, q => Assert.Null(q.TargetCriterionIds));
    }

    /// <summary>
    /// Lệch độ dài ⇒ bỏ nhãn CẢ LÔ + LogWarning — KHÔNG gán "theo index có sẵn" (nửa đúng nửa sai tệ hơn
    /// không nhãn), KHÔNG ném (câu hỏi vẫn dùng được, không 500).
    /// </summary>
    [Fact]
    public void Align_LechDoDai_BoNhanCaLo_LogWarning_KhongNem()
    {
        var a = Guid.NewGuid();
        var log = new CapturingLogger<AiServiceQuestionGenerator>();

        var got = AiServiceQuestionGenerator.AlignTargets(
            new() { "Q1", "Q2", "Q3" },
            new() { new() { a.ToString("D") }, new() { a.ToString("D") } },   // 2 ≠ 3
            log);

        Assert.Equal(3, got.Count);
        Assert.All(got, q => Assert.Null(q.TargetCriterionIds));   // kể cả 2 câu đầu "có sẵn" nhãn
        Assert.Equal(1, log.Warnings);
        Assert.Contains("lệch", log.Entries.Single(e => e.Level == LogLevel.Warning).Message);
    }

    /// <summary>Zip TRƯỚC khi lọc câu trống: câu 2 trống bị bỏ, nhãn của câu 3 phải về ĐÚNG câu 3.</summary>
    [Fact]
    public void Align_CauTrongBiLoc_NhanKhongLechIndex()
    {
        var a = Guid.NewGuid(); var c = Guid.NewGuid();
        var got = AiServiceQuestionGenerator.AlignTargets(
            new() { "Q1", "   ", "Q3" },
            new() { new() { a.ToString("D") }, new() { Guid.NewGuid().ToString("D") }, new() { c.ToString("D") } },
            NullLogger.Instance);

        Assert.Equal(2, got.Count);
        Assert.Equal("Q1", got[0].Text); Assert.Equal(new[] { a }, got[0].TargetCriterionIds);
        Assert.Equal("Q3", got[1].Text); Assert.Equal(new[] { c }, got[1].TargetCriterionIds);
    }

    // ═══════════════════ (2b) Service — fresh nhận nhãn, lớp 2 lọc id lạ ═══════════════════

    [Fact]
    public async Task Generate_FreshNhanNhan_IdLaBiBo_RongGiuRong_NullGiuNull_ChiGuiWhenTargeted()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var always = Crit("Giao tiep", CriterionScoringScope.Always, 0, 0.4m);
        var wa = Crit("Thuat toan", CriterionScoringScope.WhenTargeted, 1, 0.3m);
        var wb = Crit("He thong", CriterionScoringScope.WhenTargeted, 2, 0.3m);
        var camp = await SeedAsync(tdb, org, [always, wa, wb], []);

        var stranger = Guid.NewGuid();
        var gen = new LabelingGenerator(
            new GeneratedQuestion("Q1", new[] { wa.Id, stranger, wa.Id }),   // id lạ + trùng
            new GeneratedQuestion("Q2", new[] { always.Id }),                  // Always gửi làm nhãn ⇒ bỏ ⇒ []
            new GeneratedQuestion("Q3", Array.Empty<Guid>()),                 // [] giữ []
            new GeneratedQuestion("Q4", null));                               // null giữ null
        var log = new CapturingLogger<CampaignSvc>();

        var res = await NewService(tdb.NewContext(), gen, log).GenerateCampaignQuestionsAsync(org, org, camp.Id, count: null, default);

        // (a) dây: chỉ 2 WhenTargeted đi qua `criteria`, context đủ 3
        Assert.Equal(new[] { wa.Id, wb.Id }, gen.LastCriteria!.Select(c => c.CriterionId).ToArray());
        Assert.Equal(3, gen.LastCriteriaContext!.Count);

        // (b) lưu: id lạ + id Always bị bỏ (I1 — nhãn chỉ thu hẹp), dedup, [] ≠ null
        // 4 câu fresh cùng CreatedAt ⇒ tra theo text, không tin thứ tự Id (Guid ngẫu nhiên).
        var rows = (await QuestionsAsync(tdb, camp.Id)).ToDictionary(r => r.QuestionText);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { wa.Id }, rows["Q1"].TargetCriterionIds);
        Assert.NotNull(rows["Q2"].TargetCriterionIds); Assert.Empty(rows["Q2"].TargetCriterionIds!);
        Assert.NotNull(rows["Q3"].TargetCriterionIds); Assert.Empty(rows["Q3"].TargetCriterionIds!);
        Assert.Null(rows["Q4"].TargetCriterionIds);
        Assert.True(log.Warnings >= 1, "bỏ id lạ phải LogWarning, không nuốt im lặng");

        // (c) response: coverage đọc được dù đường này KHÔNG Include Criteria — B chưa ai nhắm
        Assert.Equal(new[] { wb.Id }, res.QuestionBank.CoverageWarnings.Select(w => w.CriterionId).ToArray());
        Assert.Equal("He thong", res.QuestionBank.CoverageWarnings.Single().Name);
        // response echo nhãn của câu (T1 DTO)
        Assert.Equal(new[] { wa.Id }, res.Questions.Single(q => q.QuestionText == "Q1").TargetCriterionIds);
    }

    [Fact]
    public async Task Generate_KhongWhenTargeted_GuiCriteriaRong_MoiNhanBiBo()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var always = Crit("Giao tiep", CriterionScoringScope.Always, 0, 1.0m);
        var camp = await SeedAsync(tdb, org, [always], []);
        var gen = new LabelingGenerator(new GeneratedQuestion("Q1", new[] { always.Id }));

        await NewService(tdb.NewContext(), gen).GenerateCampaignQuestionsAsync(org, org, camp.Id, count: null, default);

        Assert.Empty(gen.LastCriteria!);
        var row = Assert.Single(await QuestionsAsync(tdb, camp.Id));
        Assert.NotNull(row.TargetCriterionIds); Assert.Empty(row.TargetCriterionIds!);
    }

    [Fact]
    public void KeepKnownTargets_NullGiuNull_RongGiuRong_LaBo_Dedup()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var stranger = Guid.NewGuid();
        var allowed = new HashSet<Guid> { a, b };
        var dropped = 0;

        Assert.Null(CampaignSvc.KeepKnownTargets(null, allowed, ref dropped));
        var empty = CampaignSvc.KeepKnownTargets(Array.Empty<Guid>(), allowed, ref dropped);
        Assert.NotNull(empty); Assert.Empty(empty!);
        Assert.Equal(new[] { b, a }, CampaignSvc.KeepKnownTargets(new[] { b, stranger, a, b }, allowed, ref dropped));
        Assert.Equal(1, dropped);
    }

    // ═══════════════════ (3) Selector — rổ = tiêu chí CHÍNH ═══════════════════

    /// <summary>Khoá rổ: nhãn [0] (không phải cuối, không phải tất cả); không nhãn/[] ⇒ tên nhóm; null ⇒ "".</summary>
    [Fact]
    public void BucketKey_LaTieuChiChinh_Nhan0_KhongPhaiCuoi_KhongNhanThiNhom()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Equal(a.ToString("D"), QuestionPoolSelector.BucketKey(Pq("q", new() { a, b })));
        Assert.Equal(b.ToString("D"), QuestionPoolSelector.BucketKey(Pq("q", new() { b, a })));
        Assert.Equal("X", QuestionPoolSelector.BucketKey(Pq("q", null, group: "X")));
        Assert.Equal("X", QuestionPoolSelector.BucketKey(Pq("q", new(), group: "X")));   // [] = chưa nhắm ⇒ nhóm
        Assert.Equal("", QuestionPoolSelector.BucketKey(Pq("q", null, group: null)));      // I5: y như trước
    }

    /// <summary>AC-3: 2 chính A · 2 chính B · 2 không nhãn "X", K=3 ⇒ mỗi rổ đúng 1; tất định theo cặp id.</summary>
    [Fact]
    public void Select_ChiaDeuTheoTieuChiChinh_VaNhomKhongNhan_TatDinh()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var pool = new List<PoolQuestion>
        {
            Pq("A1", new() { a }), Pq("A2", new() { a, b }),
            Pq("B1", new() { b }), Pq("B2", new() { b, a }),
            Pq("X1", null, group: "X"), Pq("X2", null, group: "X"),
        };

        var first = QuestionPoolSelector.Select(pool, 3, CampaignId, CandidateId);
        var again = QuestionPoolSelector.Select(pool, 3, CampaignId, CandidateId);

        Assert.Equal(3, first.Count);
        Assert.Equal(1, first.Count(q => q.Text.StartsWith('A')));
        Assert.Equal(1, first.Count(q => q.Text.StartsWith('B')));
        Assert.Equal(1, first.Count(q => q.Text.StartsWith('X')));
        Assert.Equal(first.Select(q => q.Id), again.Select(q => q.Id));   // create-or-get: vào lại nhận đúng đề

        var other = QuestionPoolSelector.Select(pool, 3, CampaignId, Guid.NewGuid());
        Assert.Equal(1, other.Count(q => q.Text.StartsWith('A')));       // ứng viên khác vẫn đủ 3 rổ
        Assert.Equal(1, other.Count(q => q.Text.StartsWith('B')));
    }

    /// <summary>
    /// Rổ theo nhãn [0] — không phải Last(): 4 câu nhãn [C, *] chỉ khác phần tử cuối vẫn là MỘT rổ C, câu
    /// [A] và [B] là hai rổ riêng ⇒ K=3 rút mỗi rổ 1. Nếu khoá theo phần tử cuối thì 4 câu kia tách ra 2
    /// rổ A/B lẫn với hai câu đơn ⇒ có ứng viên nhận 2 câu chính C. Chạy trên nhiều ứng viên để phép
    /// khẳng định không phụ thuộc một hạt giống may mắn.
    /// </summary>
    [Fact]
    public void Select_RoTheoNhanDau_KhongPhaiNhanCuoi()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var pool = new List<PoolQuestion>
        {
            Pq("C1", new() { c, a }), Pq("C2", new() { c, a }), Pq("C3", new() { c, b }), Pq("C4", new() { c, b }),
            Pq("A", new() { a }), Pq("B", new() { b }),
        };

        for (var i = 1; i <= 40; i++)
        {
            var candidate = Guid.Parse($"00000000-0000-0000-0000-{i:D12}");
            var got = QuestionPoolSelector.Select(pool, 3, CampaignId, candidate);
            var primaries = got.Select(q => q.TargetCriterionIds![0]).Distinct().Count();
            Assert.Equal(3, primaries);   // mỗi buổi đủ 3 tiêu chí chính — đúng mục đích của rổ
        }
    }

    /// <summary>I5 — chiến dịch cũ (0 nhãn) chia theo question_group Y NHƯ TRƯỚC.</summary>
    [Fact]
    public void Select_KhongNhan_ChiaTheoNhomNhuTruoc()
    {
        var pool = new List<PoolQuestion>();
        foreach (var g in new[] { "Thuật toán", "Hệ thống", "Kinh nghiệm" })
            pool.AddRange(Enumerable.Range(1, 5).Select(i => Pq($"{g}-{i}", null, group: g)));

        var got = QuestionPoolSelector.Select(pool, 6, CampaignId, CandidateId);

        Assert.Equal(6, got.Count);
        Assert.All(got.GroupBy(q => q.Group), grp => Assert.Equal(2, grp.Count()));
    }

    /// <summary>
    /// KHE NỐI ParticipationService → selector: nhãn phải đi vào <c>PoolQuestion</c> (projection tại Start),
    /// không thì selector unit-test xanh mà production vẫn chia theo nhóm. 3 câu chính A + 3 chính B, KHÔNG
    /// question_group, K=2 ⇒ mỗi buổi đúng 1 A + 1 B trên nhiều ứng viên (một rổ chung sẽ có lúc ra A+A).
    /// </summary>
    [Fact]
    public async Task Start_TruyenNhanVaoPool_RutMoiRoMotCau()
    {
        using var tdb = new CampaignTestDb();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.QuestionsPerSession = 2;
        for (var i = 0; i < 3; i++)
        {
            camp.Questions.Add(new CampaignQuestion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId, QuestionText = $"A{i}",
                Source = QuestionSource.CustomHr, IsRequired = false, TargetCriterionIds = new() { a },
                CreatedAt = SeedEpoch.AddSeconds(i),
            });
            camp.Questions.Add(new CampaignQuestion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId, QuestionText = $"B{i}",
                Source = QuestionSource.CustomHr, IsRequired = false, TargetCriterionIds = new() { b },
                CreatedAt = SeedEpoch.AddSeconds(10 + i),
            });
        }
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = a, CampaignId = camp.Id, OrderNo = 0, Name = "A", Weight = 0.5m, MaxScore = 5,
            Source = CriterionSource.HrEdited, ScoringScope = CriterionScoringScope.WhenTargeted,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = b, CampaignId = camp.Id, OrderNo = 1, Name = "B", Weight = 0.5m, MaxScore = 5,
            Source = CriterionSource.HrEdited, ScoringScope = CriterionScoringScope.WhenTargeted,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.Campaigns.Add(camp);
        var candidates = Enumerable.Range(1, 30).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}")).ToList();
        foreach (var cand in candidates)
            tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(camp.Id, cand));
        await tdb.Db.SaveChangesAsync();

        var sent = new List<IReadOnlyList<string>>();
        var session = new Mock<ICampaignSessionClient>();
        session.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SessionQuestionInput>?>(),
                It.IsAny<CampaignScoringPolicyInput?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid _, Guid _, string _, IReadOnlyList<string> qs, IReadOnlyList<SessionCriterionInput> _,
                    DateTime? _, bool? _, int? _, int? _, int? _, string _, int _, IReadOnlyList<SessionQuestionInput>? _,
                    CampaignScoringPolicyInput? _, bool _, CancellationToken _) => sent.Add(qs))
            .ReturnsAsync(() => new CampaignSessionResult(Guid.NewGuid(), new List<SessionQuestion> { new(Guid.NewGuid(), 1, "Q", 120) }));
        var auth = new Mock<IAuthProvisionClient>();

        foreach (var cand in candidates)
        {
            var svc = new ParticipationService(tdb.NewContext(), auth.Object, session.Object,
                NullLogger<ParticipationService>.Instance);
            await svc.StartInterviewAsync(cand, camp.Id, default);
        }

        Assert.Equal(30, sent.Count);
        Assert.All(sent, qs =>
        {
            Assert.Equal(2, qs.Count);
            Assert.Equal(1, qs.Count(t => t.StartsWith('A')));
            Assert.Equal(1, qs.Count(t => t.StartsWith('B')));
        });
    }

    // ═══════════════════ (4)(5) Summary — coverage + K-rule ═══════════════════

    [Fact]
    public void Coverage_ChiWhenTargetedKhongAiNham_AlwaysKhongBaoGio_NhanViTri2VanTinh()
    {
        var always = Crit("Giao tiep", CriterionScoringScope.Always, 0, 0.4m);        // không ai nhắm — vẫn KHÔNG vào
        var wa = Crit("Thuat toan", CriterionScoringScope.WhenTargeted, 1, 0.2m);      // nhắm ở vị trí 1
        var wb = Crit("He thong", CriterionScoringScope.WhenTargeted, 2, 0.2m);        // nhắm ở vị trí 2 (không chính)
        var wc = Crit("CSDL", CriterionScoringScope.WhenTargeted, 3, 0.2m);            // không ai nhắm ⇒ cảnh báo
        var questions = new[] { Q("q1", new() { wa.Id, wb.Id }), Q("q2", null), Q("q3", new()) };

        var s = QuestionBankSummary.Build(questions, null, null, null, new[] { always, wa, wb, wc });

        var w = Assert.Single(s.CoverageWarnings);
        Assert.Equal(wc.Id, w.CriterionId);
        Assert.Equal("CSDL", w.Name);
        Assert.Empty(s.Warnings);   // coverage CHỈ cảnh báo, không vào Warnings (D-5)
    }

    [Fact]
    public void Coverage_KhongCoWhenTargeted_HoacTatCaDuocNham_Rong()
    {
        var always = Crit("Giao tiep", CriterionScoringScope.Always, 0, 0.5m);
        var wa = Crit("Thuat toan", CriterionScoringScope.WhenTargeted, 1, 0.5m);

        Assert.Empty(QuestionBankSummary.Build(new[] { Q("q", null) }, null, null, null, new[] { always }).CoverageWarnings);
        Assert.Empty(QuestionBankSummary.Build(new[] { Q("q", new() { wa.Id }) }, null, null, null, new[] { always, wa }).CoverageWarnings);
        Assert.Empty(QuestionBankSummary.Build(new[] { Q("q", null) }, null, null, null).CoverageWarnings);   // overload cũ: luôn []
    }

    [Theory]
    [InlineData(2, true)]    // K < 3 rổ chính ⇒ chặn
    [InlineData(3, false)]   // K == số rổ ⇒ đủ, KHÔNG chặn (luật là `<`, không phải `<=`)
    [InlineData(5, false)]
    public void KRule_KNhoHonSoTieuChiChinhDistinct(int k, bool expectWarning)
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var questions = new[]
        {
            Q("1", new() { a, b }), Q("2", new() { a }),      // chính A ×2
            Q("3", new() { b }), Q("4", new() { c, a }),      // chính B, chính C
            Q("5", null), Q("6", new()),                      // không nhãn: không phải rổ chính
        };

        var s = QuestionBankSummary.Build(questions, k, null, null);
        var hit = s.Warnings.Any(w => w.StartsWith(QuestionBankSummary.KBelowCriteriaGroupsCode + ":", StringComparison.Ordinal));
        Assert.Equal(expectWarning, hit);
    }

    [Fact]
    public void KRule_KNull_HoacKhongNhan_KhongBan()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var labeled = new[] { Q("1", new() { a }), Q("2", new() { b }), Q("3", new() { c }) };
        Assert.DoesNotContain(QuestionBankSummary.Build(labeled, null, null, null).Warnings,
            w => w.Contains(QuestionBankSummary.KBelowCriteriaGroupsCode));

        // I5: chiến dịch cũ 0 nhãn, K=1 với 3 nhóm tên — K-rule KHÔNG bắn (nhóm tên không phải rổ tiêu chí)
        var unlabeled = new[] { Q("1", null, group: "X"), Q("2", null, group: "Y"), Q("3", null, group: "Z") };
        Assert.DoesNotContain(QuestionBankSummary.Build(unlabeled, 1, null, null).Warnings,
            w => w.Contains(QuestionBankSummary.KBelowCriteriaGroupsCode));
    }

    [Fact]
    public async Task Publish_KDuoiSoTieuChiChinh_400_QUESTION_BANK_INVALID_GiuDraft()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var wa = Crit("A", CriterionScoringScope.WhenTargeted, 0, 0.4m);
        var wb = Crit("B", CriterionScoringScope.WhenTargeted, 1, 0.3m);
        var wc = Crit("C", CriterionScoringScope.WhenTargeted, 2, 0.3m);
        var camp = await SeedAsync(tdb, org, [wa, wb, wc],
            [Q("1", new() { wa.Id }), Q("2", new() { wb.Id }), Q("3", new() { wc.Id })],
            questionsPerSession: 2);

        var ex = await Assert.ThrowsAsync<QuestionBankInvalidException>(() =>
            NewService(tdb.NewContext()).PublishCampaignAsync(org, org, camp.Id, default));

        var body = JsonSerializer.SerializeToElement(ex.Body);
        Assert.Equal("QUESTION_BANK_INVALID", body.GetProperty("code").GetString());
        Assert.Contains(body.GetProperty("warnings").EnumerateArray(),
            w => w.GetString()!.StartsWith("K_BELOW_CRITERIA_GROUPS:", StringComparison.Ordinal));
        Assert.Equal(CampaignStatus.Draft,
            tdb.NewContext().Campaigns.AsNoTracking().Single(c => c.Id == camp.Id).Status);
    }

    /// <summary>D-5: coverage CHỈ cảnh báo — tiêu chí WhenTargeted không ai nhắm KHÔNG chặn publish.</summary>
    [Fact]
    public async Task Publish_ChiCoCoverageWarning_VanQua_KNullKhongChan()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var wa = Crit("A", CriterionScoringScope.WhenTargeted, 0, 0.5m);
        var wb = Crit("B", CriterionScoringScope.WhenTargeted, 1, 0.5m);
        var camp = await SeedAsync(tdb, org, [wa, wb],
            [Q("1", new() { wa.Id }), Q("2", new() { wa.Id }), Q("3", null)],
            questionsPerSession: null);

        var res = await NewService(tdb.NewContext()).PublishCampaignAsync(org, org, camp.Id, default);

        Assert.Equal("Active", res.Status);
        Assert.Empty(res.QuestionBank.Warnings);
        Assert.Equal(wb.Id, Assert.Single(res.QuestionBank.CoverageWarnings).CriterionId);
    }

    // ═══════════════════ (6) JSON — coverageWarnings camelCase, luôn có ═══════════════════

    [Fact]
    public async Task Get_Json_CoQuestionBankCoverageWarnings_CamelCase_RongKhiSach()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var wa = Crit("A", CriterionScoringScope.WhenTargeted, 0, 1.0m);
        var camp = await SeedAsync(tdb, org, [wa], [Q("1", new() { wa.Id })]);

        var res = await NewService(tdb.NewContext()).GetCampaignAsync(org, camp.Id, default);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(res, RuntimeJson())).RootElement;

        var cov = json.GetProperty("questionBank").GetProperty("coverageWarnings");
        Assert.Equal(JsonValueKind.Array, cov.ValueKind);
        Assert.Equal(0, cov.GetArrayLength());
        Assert.DoesNotContain(json.GetProperty("questionBank").EnumerateObject(), p => p.Name == "CoverageWarnings");
    }

    [Fact]
    public async Task Get_Json_CoverageWarnings_ShapeCriterionIdName()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var wa = Crit("A", CriterionScoringScope.WhenTargeted, 0, 0.5m);
        var wb = Crit("ZZBNAME", CriterionScoringScope.WhenTargeted, 1, 0.5m);
        var camp = await SeedAsync(tdb, org, [wa, wb], [Q("1", new() { wa.Id })]);

        var res = await NewService(tdb.NewContext()).GetCampaignAsync(org, camp.Id, default);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(res, RuntimeJson())).RootElement;

        var w = Assert.Single(json.GetProperty("questionBank").GetProperty("coverageWarnings").EnumerateArray());
        Assert.Equal(wb.Id.ToString("D"), w.GetProperty("criterionId").GetString());
        Assert.Equal("ZZBNAME", w.GetProperty("name").GetString());
    }

    /// <summary>Danh sách campaign (KHÔNG Include Criteria) vẫn báo coverage thật — không trả [] nói dối.</summary>
    [Fact]
    public async Task List_CoverageWarnings_ThatDuKhongIncludeCriteria()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var wa = Crit("A", CriterionScoringScope.WhenTargeted, 0, 0.4m);
        var wb = Crit("B", CriterionScoringScope.WhenTargeted, 1, 0.3m);
        var always = Crit("Always", CriterionScoringScope.Always, 2, 0.3m);   // CHECK weight ∈ (0,1]
        var camp = await SeedAsync(tdb, org, [wa, wb, always], [Q("1", new() { wa.Id })]);

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(org, null, null, default);

        var item = Assert.Single(page.Items, i => i.Id == camp.Id);
        Assert.Equal(wb.Id, Assert.Single(item.QuestionBank.CoverageWarnings).CriterionId);
    }

    [Fact]
    public async Task List_KhongWhenTargeted_CoverageRong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var always = Crit("Always", CriterionScoringScope.Always, 0, 1.0m);
        var camp = await SeedAsync(tdb, org, [always], [Q("1", null)]);

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(org, null, null, default);

        Assert.Empty(Assert.Single(page.Items, i => i.Id == camp.Id).QuestionBank.CoverageWarnings);
    }
}
