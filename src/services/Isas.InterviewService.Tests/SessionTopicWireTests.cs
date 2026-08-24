using System.Net;
using System.Text;
using System.Text.Json;
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
/// TOP1-B5 — danh mục đề tài phải tới được lớp SINH, được lưu, và trả về client — mà không đổi MỘT
/// BYTE hành vi của caller cũ (kill-switch tắt, pool rỗng, buổi bài học lộ trình).
///
/// Mẫu và cấu trúc lấy nguyên từ <c>LessonContextWireTests</c> (hợp đồng dây, HttpMessageHandler
/// bắt payload thật) và <c>GroundingWireTests</c> (PracticeService wire 3 nhánh: tắt/rỗng/có).
/// </summary>
public class SessionTopicWireTests
{
    // ═══════════ 1. HỢP ĐỒNG DÂY — JSON gửi sang AIService (tầng AiServiceQuestionGenerator) ═══════

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

    private static readonly List<SessionTopic> TwoTopics =
    [
        new("t1", "Xử lý race condition trong hệ thống đồng thời", TopicSource.Catalog,
            CriterionName: "Chiều sâu kỹ thuật"),
        new("t2", "Chọn giữa các phương án lưu trữ dữ liệu và caching", TopicSource.Catalog,
            CriterionName: "Thiết kế hệ thống & CSDL"),
    ];

    /// <summary>
    /// 🔒 KHOÁ TÊN KHOÁ RA DÂY: <c>topics[].label/cvLevel/cvEvidence</c>. CẤM tường minh của B5:
    /// KHÔNG được gửi <c>key</c>/<c>source</c>/<c>criterionName</c> — assert trên object C# (record
    /// equality) KHÔNG bắt được việc này, phải đọc JSON THẬT trên dây (mẫu LessonContextWireTests).
    /// </summary>
    [Fact]
    public async Task GuiTopics_RaDayDungTenKhoa_KhongLoKeySourceCriterionName()
    {
        var (gen, handler) = Generator();

        await gen.GenerateQuestionsAsync(
            "BE", cvText: null, jdText: null, focusCriteria: null, count: 5,
            grounding: null, "vi", criteria: null, "Junior", TwoTopics, default);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var arr = doc.RootElement.GetProperty("topics");
        Assert.Equal(2, arr.GetArrayLength());

        Assert.Equal("Xử lý race condition trong hệ thống đồng thời", arr[0].GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, arr[0].GetProperty("cvLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, arr[0].GetProperty("cvEvidence").ValueKind);

        foreach (var item in arr.EnumerateArray())
        {
            Assert.False(item.TryGetProperty("key", out _), "key KHÔNG được lộ ra dây");
            Assert.False(item.TryGetProperty("source", out _), "source KHÔNG được lộ ra dây");
            Assert.False(item.TryGetProperty("criterionName", out _), "criterionName KHÔNG được lộ ra dây (CẤM)");
        }
    }

    [Fact]
    public async Task GuiTopics_CvLevelCvEvidence_CoThatThiDiKemLabel()
    {
        var (gen, handler) = Generator();
        var topics = new List<SessionTopic>
        {
            new("t1", "Chủ đề A", TopicSource.CvRequirement, CriterionName: "X",
                CvLevel: "Strong", CvEvidence: "đã tối ưu hệ thống chịu 10k req/s"),
        };

        await gen.GenerateQuestionsAsync(
            "BE", null, null, null, 5, null, "vi", null, "Junior", topics, default);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var item = doc.RootElement.GetProperty("topics")[0];
        Assert.Equal("Strong", item.GetProperty("cvLevel").GetString());
        Assert.Equal("đã tối ưu hệ thống chịu 10k req/s", item.GetProperty("cvEvidence").GetString());
    }

    /// <summary>
    /// BẤT BIẾN LÙI: caller không truyền topics (mọi overload khác) không được mọc thêm khối
    /// <c>topics</c> có nội dung — mẫu <c>KhongCoBaiHoc_PayloadKhongMangLessonContext</c>.
    /// </summary>
    [Fact]
    public async Task KhongCoTopics_PayloadKhongMangTopics()
    {
        var (gen, handler) = Generator();

        await gen.GenerateQuestionsAsync("BE", null, null, "Junior");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.True(
            !doc.RootElement.TryGetProperty("topics", out var t) || t.ValueKind == JsonValueKind.Null);
    }

    // ═══════════ 2. PRACTICESERVICE — 3 nhánh kill-switch/pool/bài-học ═══════════

    private static PracticeTopic Topic(
        JobCategory cat, string seniority, string language, string key, string label,
        string? criterionName = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TopicKey = key,
            JobCategory = cat,
            Seniority = seniority,
            Language = language,
            Label = label,
            CriterionName = criterionName,
            DisplayOrder = 1,
            IsActive = true,
            Version = 1,
        };

    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, bool topicsEnabled, TopicSelector? selector = null)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, notifier.Object,
            reservation.Object, NullLogger<PracticeService>.Instance,
            topicsOptions: Options.Create(new TopicsOptions { Enabled = topicsEnabled }),
            topicSelector: selector);
    }

    // Setup cho nhánh "plain" (4 tham số + seniority + ct) — đường mà mọi 3 test dưới đây rơi vào
    // KHI topics không được chọn (kill-switch tắt / pool rỗng / bài học), giữ nguyên như trước B5.
    private static void SetupPlainOverload(Mock<IAiServiceQuestionGenerator> gen)
        => gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });

    private static void VerifyTopicsOverloadNeverCalled(Mock<IAiServiceQuestionGenerator> gen)
        => gen.Verify(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyList<SessionTopic>>(), It.IsAny<CancellationToken>()),
            Times.Never);

    /// pool RỖNG (kill-switch bật, không có PracticeTopic nào khớp) ⇒ Topics null, nhánh CŨ chạy.
    [Fact]
    public async Task Create_TopicsEnabled_PoolRong_TopicsNull_GoiNhanhCu()
    {
        using var t = new TestDb();
        var gen = new Mock<IAiServiceQuestionGenerator>();
        SetupPlainOverload(gen);

        var svc = Build(t, gen, topicsEnabled: true);
        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.FE), default);

        Assert.Null(res.Topics);
        VerifyTopicsOverloadNeverCalled(gen);
    }

    /// Kill-switch TẮT (pool CÓ dữ liệu khớp) ⇒ vẫn Topics null, TopicSelector không được động tới.
    [Fact]
    public async Task Create_TopicsDisabled_PoolCoDuLieu_TopicsNull_GoiNhanhCu()
    {
        using var t = new TestDb();
        t.Db.PracticeTopics.Add(Topic(JobCategory.FE, "Junior", "vi", "t1", "Chủ đề A"));
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        SetupPlainOverload(gen);

        var svc = Build(t, gen, topicsEnabled: false);
        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.FE), default);

        Assert.Null(res.Topics);
        VerifyTopicsOverloadNeverCalled(gen);
    }

    /// Buổi BÀI HỌC LỘ TRÌNH (kill-switch bật + pool CÓ dữ liệu) ⇒ BỎ QUA TopicSelector hẳn —
    /// Topics null, vẫn đi overload lessonContext như trước B5.
    [Fact]
    public async Task CreateLessonSession_TopicsEnabled_PoolCoDuLieu_TopicsNull_VanGoiOverloadLessonContext()
    {
        using var t = new TestDb();
        t.Db.PracticeTopics.Add(Topic(JobCategory.FE, "Junior", "vi", "t1", "Chủ đề A"));
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(),
                It.IsAny<string>(), It.IsAny<LessonContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                new List<GeneratedQuestion> { new() { Content = "Q1" } },
                new List<QuestionCitationDto>()));

        var svc = Build(t, gen, topicsEnabled: true);
        var res = await svc.CreateLessonSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.FE), Guid.NewGuid(),
            focusCriteria: null, lessonContext: new LessonContext("Tổng quan OOP", null));

        Assert.Null(res.Topics);
        VerifyTopicsOverloadNeverCalled(gen);
        gen.Verify(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(),
                It.IsAny<string>(), It.IsAny<LessonContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════ 3. ROUND-TRIP — tạo buổi ⇒ N phần tử; GET trả đúng N; buổi cũ Topics null ═══════════

    /// Pool nhỏ hơn số khe (3 topic < mặc định 5 câu) ⇒ TopicSelector trả HẾT pool (B3) ⇒ N = 3.
    /// Response CREATE và response GET phải cùng trả đúng 3 phần tử, cùng nội dung.
    [Fact]
    public async Task Create_ThenGet_RoundTrip_TraDungSoLuongTopics()
    {
        using var t = new TestDb();
        t.Db.PracticeTopics.AddRange(
            Topic(JobCategory.FE, "Junior", "vi", "t1", "Chủ đề A", "Chiều sâu kỹ thuật"),
            Topic(JobCategory.FE, "Junior", "vi", "t2", "Chủ đề B", "Giải quyết vấn đề"),
            Topic(JobCategory.FE, "Junior", "vi", "t3", "Chủ đề C"));
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        SetupPlainOverload(gen);   // targetable rỗng (không rubric seed) ⇒ vẫn rơi overload topics (giàu nhất)
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyList<SessionTopic>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                new List<GeneratedQuestion> { new() { Content = "Q1" } },
                new List<QuestionCitationDto>()));

        var candidate = Guid.NewGuid();
        var svc = Build(t, gen, topicsEnabled: true, selector: new TopicSelector(new Random(7)));
        var created = await svc.CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.FE), default);

        Assert.NotNull(created.Topics);
        Assert.Equal(3, created.Topics!.Count);
        var createdKeys = created.Topics.Select(x => x.Key).OrderBy(k => k).ToList();
        Assert.Equal(new[] { "t1", "t2", "t3" }, createdKeys);
        Assert.All(created.Topics, x => Assert.Equal(TopicSource.Catalog, x.Source));

        var fetched = await svc.GetSessionAsync(candidate, created.Id, default);
        Assert.NotNull(fetched);
        Assert.NotNull(fetched!.Topics);
        Assert.Equal(3, fetched.Topics!.Count);
        Assert.Equal(createdKeys, fetched.Topics.Select(x => x.Key).OrderBy(k => k).ToList());
    }

    /// Buổi CŨ (tạo trước khi cột topics tồn tại / bằng đường khác, Topics=null trong DB) ⇒ response
    /// Topics == null — client cũ không vỡ (không suy diễn thành mảng rỗng).
    [Fact]
    public async Task GetSession_BuoiCu_TopicsNullTrongDb_ResponseTopicsNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready, JobCategory.FE);
        // Topics KHÔNG set — mặc định null (buổi tạo trước cột này tồn tại).
        t.Db.PracticeSessions.Add(session);
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        var svc = Build(t, gen, topicsEnabled: true);

        var res = await svc.GetSessionAsync(candidate, session.Id, default);

        Assert.NotNull(res);
        Assert.Null(res!.Topics);
    }
}
