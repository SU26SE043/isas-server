using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// TEST-01 — khoá HỢP ĐỒNG DÂY của <see cref="AiServiceInterviewDecider"/> gửi sang AIService
/// <c>POST /api/v1/decide-next</c> (vế còn lại khai ở <c>app/schemas.py :: DecideNextRequest</c>).
///
/// VÌ SAO PHẢI CÓ: payload là <b>anonymous object viết tay</b>, nên tên khoá là chuỗi ký tự lập trình
/// viên gõ ra chứ không phải thứ được suy từ DTO. Gõ sai một khoá thì:
///   • .NET không lỗi (anonymous object nào cũng serialize được),
///   • Python không lỗi (<c>DecideNextRequest</c> không set <c>model_config</c> ⇒ pydantic
///     <c>extra='ignore'</c> NUỐT IM LẶNG field lạ, field thiếu rơi về default 0/None),
///   • prompt chạy như chưa từng có tính năng — INT-17b tắt câm mà không ai thấy.
/// Đúng lớp bug đã làm <c>focusCriteria</c> (BC14) hỏng nhiều tuần và làm entitlement
/// <c>adaptiveMaxQuestions</c> vs <c>maxQuestions</c> trả trần 0 cho mọi gói trả phí.
///
/// Trước bộ test này, <c>grep rootQuestion</c> trong project test ra 0 kết quả: các test INT-17b khác
/// (<c>AdaptiveChainDepthInt17bTests</c>) mock <c>IAiServiceInterviewDecider</c> nên KHÔNG lần nào
/// chạm class dựng payload. Đây là chỗ duy nhất khoá tên khoá thật đi trên dây.
///
/// 🔎 ĐO ĐƯỢC khi chạy mutation (đừng suy lại từ đầu): đổi <c>rootQuestion</c> thành <c>RootQuestion</c>
/// (Pascal) thì dây KHÔNG đổi — <c>JsonContent.Create(payload)</c> gọi không kèm options nên
/// <c>System.Net.Http.Json</c> lấy mặc định <c>JsonSerializerDefaults.Web</c>, tức camelCase policy vẫn
/// áp và tự nắn Pascal về camel. (Field <c>Json</c> khai đầu class chỉ dùng cho chiều ĐỌC response.)
/// Vậy rủi ro thật hẹp hơn "gõ hoa/thường tùy ý": chỉ những sai mà camelCasing KHÔNG nắn được mới lọt —
/// sai chính tả (<c>rootQuestionX</c>) và chuỗi thường liền (<c>maxdepth</c>). Cả hai đều bị bắt.
///
/// ⚠ Giá trị assert dùng SENTINEL ASCII: <c>System.Text.Json</c> escape ký tự non-ASCII
/// (<c>ế</c>…) nên so chuỗi tiếng Việt vào JSON đã serialize sẽ xanh một cách vô nghĩa.
/// </summary>
public sealed class DecideNextWireContractTests
{
    // ── Stub bắt request ────────────────────────────────────────────────────────
    // Mẫu CapturingHandler của Isas.CampaignService.Tests/CampaignAdaptiveToggleTests (project test này
    // trước đó chỉ có handler bắt URI/trả body, chưa có cái nào đọc request content).
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? InternalToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            InternalToken = request.Headers.TryGetValues("X-Internal-Token", out var values)
                ? string.Join(",", values)
                : null;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            // Response tối thiểu hợp lệ: chỉ cần `action` để decider không ném "thiếu action".
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"action":"end"}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private static (AiServiceInterviewDecider sut, CapturingHandler handler) Make(string token = "TKN_SENTINEL")
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiservice.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();
        return (new AiServiceInterviewDecider(http, config, NullLogger<AiServiceInterviewDecider>.Instance), handler);
    }

    // Mọi số ĐỀU KHÁC NHAU: nối nhầm dây (vd `currentDepth = request.MaxDepth`) là lộ ngay,
    // chứ không lọt vì hai bên tình cờ cùng giá trị.
    private static AdaptiveDecisionRequest SentinelRequest(IReadOnlyList<string>? otherTopics) => new(
        AudioObjectKey: "answer-audio/AUDIO_KEY_SENTINEL.webm",
        JobCategory: "BE",
        CurrentQuestion: "CURRENT_Q_SENTINEL",
        History: [new DecideTurnDto("HIST_Q_SENTINEL", "HIST_A_SENTINEL", "Seed")],
        AskedCount: 7,
        FollowUpCount: 3,
        MaxQuestions: 19,
        MaxFollowUps: 4,
        Criteria: [new DecideCriterionDto("CRIT_NAME_SENTINEL", "CRIT_DESC_SENTINEL")],
        RootQuestion: "ROOT_Q_SENTINEL",
        CurrentDepth: 2,
        MaxDepth: 6,
        OtherTopics: otherTopics);

    private static readonly string[] DefaultTopics = ["TOPIC_A_SENTINEL", "TOPIC_B_SENTINEL"];

    // ⚠ KHÔNG cho `otherTopics` mặc định null rồi `??=`: ca cần test CHÍNH LÀ null tường minh, gộp hai
    // ý nghĩa vào một tham số là test null chạy nhầm nhánh có dữ liệu (đã dính một lần).
    private static async Task<(JsonDocument doc, CapturingHandler handler)> SendAsync(
        IReadOnlyList<string>? otherTopics)
    {
        var (sut, handler) = Make();
        await sut.DecideNextAsync(SentinelRequest(otherTopics));
        Assert.NotNull(handler.Body);
        return (JsonDocument.Parse(handler.Body!), handler);
    }

    // Kiểm SỰ TỒN TẠI theo ĐÚNG TÊN — không so chuỗi cả body (đổi thứ tự field là vỡ vô cớ).
    private static JsonElement Prop(JsonElement parent, string name)
    {
        Assert.True(parent.TryGetProperty(name, out var el),
            $"Payload /decide-next thiếu khoá '{name}'. Đổi/gõ sai tên khoá trong " +
            "AiServiceInterviewDecider là pydantic (extra='ignore') nuốt im lặng, không lỗi ở đâu cả.");
        return el;
    }

    // ── (1) 4 khoá INT-17b — lý do trực tiếp bộ test này tồn tại ────────────────
    [Fact]
    public async Task Payload_MangDuBonKhoaInt17b_DungTen()
    {
        var (doc, _) = await SendAsync(DefaultTopics);
        using var _d = doc;
        var root = doc.RootElement;

        Assert.Equal("ROOT_Q_SENTINEL", Prop(root, "rootQuestion").GetString());
        Assert.Equal(2, Prop(root, "currentDepth").GetInt32());
        Assert.Equal(6, Prop(root, "maxDepth").GetInt32());

        var topics = Prop(root, "otherTopics");
        Assert.Equal(JsonValueKind.Array, topics.ValueKind);
        Assert.Equal(DefaultTopics, topics.EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    // ── (2) Khoá cũ — khoá TRỌN hợp đồng, không chỉ phần mới ────────────────────
    [Fact]
    public async Task Payload_MangDuKhoaCu_HopDongTronVen()
    {
        var (doc, _) = await SendAsync(DefaultTopics);
        using var _d = doc;
        var root = doc.RootElement;

        Assert.Equal("BE", Prop(root, "jobCategory").GetString());
        Assert.Equal("answer-audio/AUDIO_KEY_SENTINEL.webm", Prop(root, "audioObjectKey").GetString());
        Assert.Equal("CURRENT_Q_SENTINEL", Prop(root, "currentQuestion").GetString());
        Assert.Equal(7, Prop(root, "askedCount").GetInt32());
        Assert.Equal(3, Prop(root, "followUpCount").GetInt32());
        Assert.Equal(19, Prop(root, "maxQuestions").GetInt32());
        Assert.Equal(4, Prop(root, "maxFollowUps").GetInt32());
        Assert.Equal("Junior", Prop(root, "seniority").GetString());
        Assert.Empty(Prop(root, "currentEvidenceState").EnumerateArray());

        // Phần tử lồng cũng phải đúng tên (DecideTurn / DecideCriterion bên schemas.py).
        var turn = Assert.Single(Prop(root, "history").EnumerateArray().ToList());
        Assert.Equal("HIST_Q_SENTINEL", Prop(turn, "question").GetString());
        Assert.Equal("HIST_A_SENTINEL", Prop(turn, "answer").GetString());
        Assert.Equal("Seed", Prop(turn, "kind").GetString());

        var crit = Assert.Single(Prop(root, "criteria").EnumerateArray().ToList());
        Assert.Equal("CRIT_NAME_SENTINEL", Prop(crit, "name").GetString());
        Assert.Equal("CRIT_DESC_SENTINEL", Prop(crit, "description").GetString());
    }

    // ── (3) KHÔNG khoá lạ: mọi khoá gửi đi phải có mặt trong DecideNextRequest ──
    // Bắt cả chiều ngược lại của bug: khoá gõ sai không chỉ làm mất field mới, nó còn ĐI trên dây rồi
    // bị nuốt lặng. Thêm field ⇒ khai ở app/schemas.py TRƯỚC, rồi mới cập nhật danh sách này.
    [Fact]
    public async Task Payload_KhongCoKhoaLa_MoiKhoaDeuKhaiOSchemaPython()
    {
        var (doc, _) = await SendAsync(DefaultTopics);
        using var _d = doc;

        string[] expected =
        [
            "jobCategory", "audioObjectKey", "currentQuestion", "history", "language",
            "askedCount", "followUpCount", "maxQuestions", "maxFollowUps", "criteria",
            "rootQuestion", "currentDepth", "maxDepth", "otherTopics", "seniority", "currentEvidenceState"
        ];

        var actual = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), actual);
    }

    // ── (4) otherTopics null → `[]`, KHÔNG phải `null` ──────────────────────────
    // Python khai `otherTopics: list[str] = []`: default chỉ áp khi khoá VẮNG. Gửi JSON `null` tường
    // minh là validation error ⇒ 422 ⇒ AiServiceException ⇒ degrade về luồng tĩnh, mất câu đào sâu.
    [Fact]
    public async Task Payload_OtherTopicsNull_RaMangRong_KhongPhaiNull()
    {
        var (doc, _) = await SendAsync(otherTopics: null);
        using var _d = doc;

        var topics = Prop(doc.RootElement, "otherTopics");
        Assert.Equal(JsonValueKind.Array, topics.ValueKind);
        Assert.Empty(topics.EnumerateArray());
    }

    // ── (5) Đường dẫn + cổng GEN-7 ──────────────────────────────────────────────
    [Fact]
    public async Task Request_DungDuongDan_VaMangInternalToken()
    {
        var (doc, handler) = await SendAsync(DefaultTopics);
        using var _d = doc;

        Assert.Equal("/api/v1/decide-next", handler.RequestUri!.AbsolutePath);
        Assert.Equal("TKN_SENTINEL", handler.InternalToken);   // /decide-next fail-closed nếu thiếu
    }
}
