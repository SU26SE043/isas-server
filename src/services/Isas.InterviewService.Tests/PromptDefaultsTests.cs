using System.Net;
using Isas.InterviewService.Data;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// F21 — bản mặc định prompt cho màn admin (2026-09-16). Ba tính chất: (1) có provider ⇒ mỗi khoá
/// mang <c>DefaultBody</c> đúng; (2) provider trả null (AIService chết) ⇒ <c>DefaultBody = null</c>
/// cho MỌI khoá và ListAsync vẫn trả đủ khoá — fail-open; (3) khoá thiếu trong bản đồ ⇒ null,
/// KHÔNG phải "" (rỗng nghĩa là "mặc định trống", thiếu là lệch hợp đồng).
/// </summary>
public class PromptDefaultsTests
{
    private sealed class FakeProvider(IReadOnlyDictionary<string, string>? map) : IPromptDefaultsProvider
    {
        public int Calls;
        public Task<IReadOnlyDictionary<string, string>?> GetDefaultsAsync(CancellationToken ct = default)
        { Calls++; return Task.FromResult(map); }
    }

    [Fact]
    public async Task List_GhepDefaultBody_TheoKhoa_VaKhoaThieuLaNull()
    {
        using var t = new TestDb();
        var provider = new FakeProvider(new Dictionary<string, string>
        {
            [PromptTemplateKeys.QuestionsIntro] = "Bạn là một interviewer chuyên nghiệp cho vị trí {role}.",
            [PromptTemplateKeys.QuestionsGuidance] = "",
        });
        var svc = new PromptTemplateService(t.Db, NullLogger<PromptTemplateService>.Instance, provider);
        await svc.UpsertAsync(PromptTemplateKeys.QuestionsIntro, "Bạn là người hỏi.", Guid.NewGuid(), null, default);

        var all = await svc.ListAsync(default);

        var intro = all.Single(x => x.Key == PromptTemplateKeys.QuestionsIntro);
        Assert.Equal("Bạn là người hỏi.", intro.Body);                       // bản đang chạy (đã sửa)
        Assert.Equal("Bạn là một interviewer chuyên nghiệp cho vị trí {role}.", intro.DefaultBody); // bản mặc định vẫn kèm
        Assert.Equal("", all.Single(x => x.Key == PromptTemplateKeys.QuestionsGuidance).DefaultBody);   // khe THÊM: rỗng thật
        Assert.Null(all.Single(x => x.Key == PromptTemplateKeys.ScoringPersona).DefaultBody);            // thiếu trong bản đồ ⇒ null
        Assert.Equal(1, provider.Calls);                                     // một lượt cho cả danh sách
    }

    [Fact]
    public async Task List_ProviderTraNull_FailOpen_VanDuKhoa_DefaultBodyNull()
    {
        using var t = new TestDb();
        var svc = new PromptTemplateService(t.Db, NullLogger<PromptTemplateService>.Instance, new FakeProvider(null));

        var all = await svc.ListAsync(default);

        Assert.Equal(PromptTemplateKeys.All.Count, all.Count);
        Assert.All(all, x => Assert.Null(x.DefaultBody));
    }

    // ── Client HTTP: fail-open thật, không chỉ ở tầng service ─────────────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests.Add(request); return Task.FromResult(respond(request)); }
    }

    private static AiServicePromptDefaultsClient Client(HttpMessageHandler handler, string? baseUrl = "http://aiapi:8000/")
    {
        AiServicePromptDefaultsClient.ResetCache();
        var http = new HttpClient(handler);
        if (baseUrl is not null) http.BaseAddress = new Uri(baseUrl);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "secret", ["PromptDefaults:CacheTtlSeconds"] = "300",
        }).Build();
        return new AiServicePromptDefaultsClient(http, config, NullLogger<AiServicePromptDefaultsClient>.Instance);
    }

    [Fact]
    public async Task Client_GoiDungDuong_KemToken_VaCache()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"defaults":{"questions.intro":"X {role}."},"placeholders":{"role":"..."}}""", System.Text.Encoding.UTF8, "application/json"),
        });
        var client = Client(handler);

        var first = await client.GetDefaultsAsync();
        var second = await client.GetDefaultsAsync();

        Assert.Equal("X {role}.", first!["questions.intro"]);
        Assert.Same(first, second);                                          // cache: không gọi lại trong TTL
        Assert.Single(handler.Requests);
        Assert.Equal("http://aiapi:8000/api/v1/prompt-defaults", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("secret", handler.Requests[0].Headers.GetValues("X-Internal-Token").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Client_AIServiceLoi_TraNull_KhongNem(HttpStatusCode status)
    {
        var client = Client(new StubHandler(_ => new HttpResponseMessage(status)));
        Assert.Null(await client.GetDefaultsAsync());
    }

    [Fact]
    public async Task Client_MangHong_TraNull_KhongNem()
    {
        var client = Client(new StubHandler(_ => throw new HttpRequestException("connection refused")));
        Assert.Null(await client.GetDefaultsAsync());
    }

    [Fact]
    public async Task Client_PhanHoiThieuDefaults_TraNull_KhongCoiLaRong()
    {
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"something":"else"}""", System.Text.Encoding.UTF8, "application/json"),
        }));
        Assert.Null(await client.GetDefaultsAsync());
    }

    [Fact]
    public async Task Client_ChuaCauHinhBaseUrl_TraNull_KhongGoiMang()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Client(handler, baseUrl: null);
        Assert.Null(await client.GetDefaultsAsync());
        Assert.Empty(handler.Requests);
    }
}
