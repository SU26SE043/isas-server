using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// SEN1 — <c>seniority</c> phải tới được <c>/generate-questions</c>, không chỉ <c>/decide-next</c>.
///
/// <para><b>Vấn đề đang sống</b> (đo 2026-08-08): mức kinh nghiệm đã có ở
/// <c>practice_sessions.seniority</c>, có CHECK constraint, được validate trước <c>ReserveAsync</c>
/// và đã đi vào <c>/decide-next</c> — nhưng KHÔNG BAO GIỜ tới đường sinh câu hỏi. Ứng viên chọn
/// <i>Senior</i> nhận bộ CÂU GỐC y hệt <i>Fresher</i>, mà câu gốc mới là thứ định khung cả buổi
/// (mặc định 5/20 câu, và INT-17b cho mỗi câu gốc đào sâu tối đa 3 tầng quanh chính chủ đề nó mở
/// ra) ⇒ lựa chọn người dùng vừa trả tiền bị bỏ qua ở đúng phần quan trọng nhất.</para>
///
/// <para>Hai lớp được khoá ở đây:</para>
/// <list type="number">
///   <item><b>HỢP ĐỒNG DÂY</b> — literal <c>"seniority"</c> camelCase trong JSON gửi đi. Payload
///   dựng bằng anonymous object và <c>JsonContent.Create</c> KHÔNG áp naming policy nào, nên tên
///   khoá ra dây chỉ được quyết định ở đúng một chỗ. Lệch tên KHÔNG ném lỗi ở đâu cả — pydantic
///   <c>extra='ignore'</c> im lặng bỏ field (repo đã dính 3 lần: <c>focusCriteria</c>/BC14 ·
///   <c>metricsVersion</c> · <c>adaptiveMaxQuestions</c>).</item>
///   <item><b>MỌI NHÁNH của PracticeService</b> — service chọn overload theo 4 nhánh (labeled /
///   grounded / focus-hoặc-count / plain) và nhánh nào cũng có người dùng thật: rubric campaign B2B
///   và rubric riêng BC16 đều nhận DEFAULT <c>ScoringScope='Always'</c> nên <c>targetable</c> RỖNG
///   ⇒ chúng KHÔNG đi nhánh labeled. Wire mỗi overload giàu nhất là bỏ rơi trọn dòng B2B.</item>
/// </list>
/// </summary>
public class SeniorityWireSen1Tests
{
    // ═════════════════ 1. HỢP ĐỒNG DÂY — JSON gửi sang AIService ═════════════════

    private sealed class CaptureHandler(string json) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (AiServiceQuestionGenerator gen, CaptureHandler handler) Generator()
    {
        var handler = new CaptureHandler("""{"questions":["Q1"]}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000") };
        return (new AiServiceQuestionGenerator(
            http, new ConfigurationBuilder().Build(),
            NullLogger<AiServiceQuestionGenerator>.Instance), handler);
    }

    private static string SeniorityOf(CaptureHandler handler)
    {
        using var doc = JsonDocument.Parse(handler.LastBody!);
        return doc.RootElement.GetProperty("seniority").GetString()!;
    }

    /// <summary>
    /// 🔒 KHOÁ TÊN KHOÁ RA DÂY. Hợp đồng với pydantic là <c>seniority</c>; đổi tên (vd
    /// <c>seniorityLevel</c>) KHÔNG ném lỗi ở đâu cả — <c>extra='ignore'</c> nuốt im lặng.
    ///
    /// <para>⚠ Đính chính một hiểu nhầm đang lưu hành trong repo (probe thật, không suy luận):
    /// <c>JsonContent.Create</c> dùng <c>JsonSerializerDefaults.Web</c> nên CÓ áp camelCase — viết
    /// <c>Seniority</c> trong anonymous object vẫn ra <c>"seniority"</c>. Vì thế một phép đối chứng
    /// kiểu "không có khoá PascalCase" là VÔ NGHĨA ở đây (luôn đúng, không chứng minh gì). Phép có
    /// nghĩa là khoá đúng TÊN, và mutation tương ứng phải đổi tên chứ không đổi hoa/thường.</para>
    /// </summary>
    [Fact]
    public async Task Wire_GuiDungTenKhoaSeniority()
    {
        var (gen, handler) = Generator();

        await gen.GenerateQuestionsAsync("BE", null, null, null, null, null, "vi", null, "Senior", default);

        Assert.Equal("Senior", SeniorityOf(handler));
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Contains("seniority", doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("Fresher")]
    [InlineData("Junior")]
    [InlineData("Middle")]
    [InlineData("Senior")]
    public async Task Wire_MoiMucDeuRaDayNguyenVan(string level)
    {
        var (gen, handler) = Generator();
        await gen.GenerateQuestionsAsync("BE", null, null, null, null, null, "vi", null, level, default);
        Assert.Equal(level, SeniorityOf(handler));
    }

    /// <summary>
    /// Overload 4 tham số (nhánh "plain" của PracticeService) — nhánh này phục vụ rubric KHÔNG có
    /// tiêu chí nội dung nào, tức B2B + rubric riêng BC16. Bỏ sót nó là bỏ sót đúng nhóm đó.
    /// </summary>
    [Fact]
    public async Task Wire_Overload4ThamSo_CoSeniority()
    {
        var (gen, handler) = Generator();
        await gen.GenerateQuestionsAsync("BE", null, null, "Middle", default);
        Assert.Equal("Middle", SeniorityOf(handler));
    }

    /// Overload focusCriteria + count (nhánh bài học roadmap BC14 / F2b).
    [Fact]
    public async Task Wire_OverloadFocusCount_CoSeniority()
    {
        var (gen, handler) = Generator();
        await gen.GenerateQuestionsAsync("BE", null, null, new[] { "Tiêu chí A" }, 3, "Fresher", default);
        Assert.Equal("Fresher", SeniorityOf(handler));
    }

    /// Overload có `language` (đường grounded + đường non-vi).
    [Fact]
    public async Task Wire_OverloadLanguage_CoSeniority()
    {
        var (gen, handler) = Generator();
        await gen.GenerateQuestionsAsync("BE", null, null, null, null, null, "en", "Senior", default);

        Assert.Equal("Senior", SeniorityOf(handler));
        // `language` KHÔNG được nuốt bởi tham số mới đứng cạnh nó (hai chuỗi liền kề = chỗ dễ lệch nhất).
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("en", doc.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public async Task Wire_KhongTruyen_GuiJunior()
    {
        var (gen, handler) = Generator();
        await gen.GenerateQuestionsAsync("BE", null, null, default);
        Assert.Equal("Junior", SeniorityOf(handler));
    }

    /// <summary>
    /// 🔒 KHÔNG BAO GIỜ để null/rỗng ra dây: <c>GenerateQuestionsRequest.seniority</c> bên Python khai
    /// <c>str</c> (không Optional) ⇒ <c>"seniority": null</c> là <b>422</b>, mà đường sinh câu hỏi nằm
    /// SAU <c>ReserveAsync</c> ⇒ một chuỗi rỗng lọt xuống đây thành buổi hỏng ĐÃ TRỪ CREDIT (PAY-5).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Wire_SeniorityRong_VanGuiJunior_KhongBaoGioNull(string? bad)
    {
        var (gen, handler) = Generator();
        await gen.GenerateQuestionsAsync("BE", null, null, bad!, default);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var v = doc.RootElement.GetProperty("seniority");
        Assert.NotEqual(JsonValueKind.Null, v.ValueKind);
        Assert.Equal("Junior", v.GetString());
    }

    /// <summary>
    /// Giá trị lạ NHƯNG khác rỗng thì gửi NGUYÊN VĂN — AIService tự hạ về "Junior" và GHI LOG.
    /// Nắn ở đây là bịt mất đúng chỗ phát hiện được caller đang gửi sai (fail-open có dấu vết).
    /// </summary>
    [Fact]
    public async Task Wire_GiaTriLa_GuiNguyenVan_DeAIServiceGhiLog()
    {
        var (gen, handler) = Generator();
        await gen.GenerateQuestionsAsync("BE", null, null, "CEO", default);
        Assert.Equal("CEO", SeniorityOf(handler));
    }

    // ═════════════════ 2. MỌI NHÁNH của PracticeService đều truyền session.Seniority ═════════════════

    private static RubricCriterion Criterion(string name, ScoringScope scope) => new()
    {
        Id = Guid.NewGuid(), Name = name, Weight = 0.1m, MaxScore = 5,
        IsActive = true, JobCategory = JobCategory.BE, Language = "vi", ScoringScope = scope
    };

    /// <param name="contentCount">0 ⇒ `targetable` rỗng ⇒ KHÔNG đi nhánh labeled (đúng hình dạng
    /// rubric campaign B2B và rubric riêng BC16, cả hai đều nhận DEFAULT `Always`).</param>
    private static void SeedRubric(TestDb t, int contentCount)
    {
        var all = new List<RubricCriterion>
        {
            Criterion("Giao tiếp & trình bày", ScoringScope.Always),
            Criterion(B2CRubricSeed.FluencyName, ScoringScope.Always),
        };
        all.AddRange(Enumerable.Range(1, contentCount)
            .Select(i => Criterion($"Tiêu chí nội dung {i}", ScoringScope.WhenTargeted)));
        t.Db.RubricCriteria.AddRange(all);
        t.Db.SaveChanges();
    }

    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, AdaptiveOptions? adaptive = null)
    {
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance,
            Options.Create(adaptive ?? new AdaptiveOptions { Enabled = false }));
    }

    private static readonly List<GeneratedQuestion> Q =
        [new GeneratedQuestion { Content = "Q1" }, new GeneratedQuestion { Content = "Q2" }];

    /// <summary>
    /// Nhánh LABELED — rubric CÓ tiêu chí nội dung (rubric seed B2C). Overload giàu nhất.
    /// </summary>
    [Fact]
    public async Task Branch_Labeled_TruyenSeniorityCuaBuoi()
    {
        using var t = new TestDb();
        SeedRubric(t, contentCount: 2);

        string? seen = null;
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, string? _, string? _, IReadOnlyList<string>? _, int? _,
                       IReadOnlyList<GroundingChunk>? _, string _,
                       IReadOnlyList<QuestionTargetCriterionDto>? _, string s, CancellationToken _) => seen = s)
            .ReturnsAsync(new GeneratedQuestionsResult(Q, Array.Empty<QuestionCitationDto>()));

        await Build(t, gen).CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, Seniority: "Senior"),
            default);

        Assert.Equal("Senior", seen);
    }

    /// <summary>
    /// Nhánh FOCUS-HOẶC-COUNT — rubric KHÔNG có tiêu chí nội dung + có `questionCount`.
    /// Đây là đường của B2B và của rubric riêng BC16; wire sót nhánh này = bỏ rơi cả dòng B2B.
    /// </summary>
    [Fact]
    public async Task Branch_FocusHoacCount_TruyenSeniorityCuaBuoi()
    {
        using var t = new TestDb();
        SeedRubric(t, contentCount: 0);

        string? seen = null;
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string? _, string? _, IReadOnlyList<string>? _, int? _,
                       string s, CancellationToken _) => seen = s)
            .ReturnsAsync(Q);

        await Build(t, gen).CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 2, Seniority: "Middle"),
            default);

        Assert.Equal("Middle", seen);
    }

    /// <summary>
    /// Nhánh PLAIN — rubric không có tiêu chí nội dung, không focus, không `questionCount`.
    /// </summary>
    [Fact]
    public async Task Branch_Plain_TruyenSeniorityCuaBuoi()
    {
        using var t = new TestDb();
        SeedRubric(t, contentCount: 0);

        string? seen = null;
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string? _, string? _, string s, CancellationToken _) => seen = s)
            .ReturnsAsync(Q);

        await Build(t, gen).CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, Seniority: "Fresher"),
            default);

        Assert.Equal("Fresher", seen);
    }

    /// <summary>
    /// Không khai `seniority` ⇒ session đóng dấu "Junior" (ValidateSeniority) ⇒ đúng thứ đó đi tiếp.
    /// Nếu chỗ này ra <c>null</c>/rỗng thì payload sẽ mang null và AIService trả 422 trên một buổi
    /// đã trừ credit.
    /// </summary>
    [Fact]
    public async Task Branch_KhongKhaiSeniority_TruyenJunior_KhongPhaiNull()
    {
        using var t = new TestDb();
        SeedRubric(t, contentCount: 0);

        string? seen = null;
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string? _, string? _, string s, CancellationToken _) => seen = s)
            .ReturnsAsync(Q);

        var res = await Build(t, gen).CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE), default);

        Assert.Equal("Junior", seen);
        // Cùng một sự thật ở hai chỗ: thứ đi vào AI PHẢI là thứ đã đóng dấu xuống DB, không phải một
        // hằng số thứ hai chép tay ở call site (chép tay = hai nguồn sự thật, lệch nhau lúc nào không hay).
        var stored = await t.NewContext().PracticeSessions.AsNoTracking().SingleAsync(s => s.Id == res.Id);
        Assert.Equal(stored.Seniority, seen);
    }
}
