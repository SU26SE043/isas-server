using System.Net;
using System.Text;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-16 — client gọi AIService soạn mốc: LỖI PHẢI NỔI LÊN, không được nuốt.
///
/// <para>⚠ Mẫu copy-paste trong repo dẫn thẳng vào hành vi sai: <c>AiServiceCriteriaSuggester</c>
/// bắt <c>Exception</c> rồi <c>return null</c> để tầng trên rơi về <c>BuildDefaultCriteria</c>. Sao
/// nguyên mẫu đó sang đây nghĩa là AI hỏng mà HR vẫn thấy một bộ mốc trên màn hình và TIN rằng AI
/// viết nó — rồi publish một thước đo chưa ai viết. "Chưa có mốc" vốn là trạng thái hợp lệ nên
/// fail-loud không chặn ai làm việc.</para>
///
/// <para>Đây là lý do có test ở tầng CLIENT chứ không chỉ tầng service: mock service không quan sát
/// được việc client nuốt lỗi.</para>
/// </summary>
public class AiServiceLevelSuggesterTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _factory;
        public int Calls { get; private set; }
        public StubHandler(Func<int, HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_factory(Calls));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static AiServiceLevelSuggester NewClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return new AiServiceLevelSuggester(http, config, NullLogger<AiServiceLevelSuggester>.Instance);
    }

    private static readonly IReadOnlyList<LevelSuggestionInput> Input =
        new List<LevelSuggestionInput> { new(Guid.NewGuid(), "Chuyên môn", null, 5) };

    // ⚠ TEST QUAN TRỌNG NHẤT CỦA FILE: AI 500 ⇒ NÉM (→502), tuyệt đối không trả bộ mốc nào.
    [Fact]
    public async Task AI_tra_500_thi_NEM_chu_khong_tra_bo_moc_mac_dinh()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"));

        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewClient(handler).SuggestAsync("BE", "vi", "Junior", null, Input, default));

        // Nhân nhượng cho lỗi chớp nhoáng: thử lại ĐÚNG một lần rồi mới bỏ cuộc.
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AI_khong_goi_duoc_thi_NEM()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewClient(handler).SuggestAsync("BE", "vi", null, null, Input, default));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AI_tra_JSON_hong_thi_NEM()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "khong-phai-json"));
        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewClient(handler).SuggestAsync("BE", "vi", null, null, Input, default));
    }

    // Trả 200 nhưng rỗng ruột cũng là hỏng — im lặng trả danh sách rỗng thì HR tưởng AI "không nghĩ ra
    // mốc nào" thay vì biết là hệ thống lỗi.
    [Fact]
    public async Task AI_tra_200_nhung_rong_thi_NEM()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"criteria\":[]}"));
        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewClient(handler).SuggestAsync("BE", "vi", null, null, Input, default));
    }

    // Lỗi chớp nhoáng: lượt 1 hỏng, lượt 2 được ⇒ trả kết quả, không phiền HR.
    [Fact]
    public async Task Loi_chop_nhoang_thi_luot_hai_cuu_duoc()
    {
        var id = Input[0].CriterionId;
        var handler = new StubHandler(call => call == 1
            ? Json(HttpStatusCode.ServiceUnavailable, "{}")
            : Json(HttpStatusCode.OK,
                $"{{\"criteria\":[{{\"criterionId\":\"{id}\",\"levels\":[{{\"score\":0,\"descriptor\":\"mức 0\"}},{{\"score\":5,\"descriptor\":\"mức 5\"}}]}}]}}"));

        var res = await NewClient(handler).SuggestAsync("BE", "vi", null, null, Input, default);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(new[] { 0, 5 }, Assert.Single(res).Levels.Select(l => l.Score));
    }

    [Fact]
    public async Task Gui_kem_X_Internal_Token_va_dung_duong_dan()
    {
        string? path = null, token = null;
        var id = Input[0].CriterionId;
        var handler = new CapturingHandler(
            $"{{\"criteria\":[{{\"criterionId\":\"{id}\",\"levels\":[{{\"score\":0,\"descriptor\":\"a\"}}]}}]}}",
            (p, t) => { path = p; token = t; });

        await NewClient(handler).SuggestAsync("BE", "vi", null, null, Input, default);

        Assert.Equal("/api/v1/suggest-criterion-levels", path);
        Assert.Equal("tkn", token);   // GEN-7: endpoint AIService gate fail-closed
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly Action<string?, string?> _capture;
        public int Calls { get; private set; }
        public CapturingHandler(string body, Action<string?, string?> capture)
        {
            _body = body; _capture = capture;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            _capture(req.RequestUri?.AbsolutePath,
                req.Headers.TryGetValues("X-Internal-Token", out var v) ? v.FirstOrDefault() : null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
