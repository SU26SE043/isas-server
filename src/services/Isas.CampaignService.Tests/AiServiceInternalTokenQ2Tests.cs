using System.Net;
using System.Text;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Q2/GEN-7 — client Campaign gọi AIService phải đính <c>X-Internal-Token</c>.
///
/// <see cref="AiServiceFaceVerifyClient"/> đã có test riêng
/// (<see cref="AiServiceFaceVerifyClientTokenTests"/>); file này phủ 2 client còn lại.
///
/// ⚠ <see cref="AiServiceCriteriaSuggester"/> là ca dễ hỏng CÂM nhất trong cả repo: nó nuốt mọi lỗi
/// và trả <c>null</c> để CampaignService fallback về bộ tiêu chí mặc định. Thiếu token ⇒ AIService
/// 401 ⇒ HR vẫn publish được campaign, chỉ là tiêu chí không phải do AI đề xuất — không lỗi, không
/// 500, không ai biết. Vì thế header phải bị khoá bằng test chứ không dựa vào e2e.
/// </summary>
public class AiServiceInternalTokenQ2Tests
{
    private const string Token = "tok-campaign-q2";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public HttpRequestMessage? Last { get; private set; }

        public CapturingHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = Token })
            .Build();

    private static HttpClient Http(CapturingHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://ai.test") };

    private static void AssertTokenSent(CapturingHandler handler, string expectedPath)
    {
        Assert.NotNull(handler.Last);
        Assert.Equal(expectedPath, handler.Last!.RequestUri?.AbsolutePath);
        Assert.True(handler.Last.Headers.TryGetValues("X-Internal-Token", out var values),
            $"Thiếu X-Internal-Token khi gọi {expectedPath} — AIService gate fail-closed sẽ trả 401.");
        Assert.Equal(Token, Assert.Single(values!));
    }

    [Fact]
    public async Task CriteriaSuggester_dinh_X_Internal_Token()
    {
        var handler = new CapturingHandler(
            """{"criteria":[{"name":"Kỹ thuật","description":null,"weight":1.0,"maxScore":5}]}""");
        var sut = new AiServiceCriteriaSuggester(
            Http(handler), Config(), NullLogger<AiServiceCriteriaSuggester>.Instance);

        var result = await sut.SuggestAsync("BE", "JD", null, 4);

        // Không chỉ assert header: nếu client nuốt lỗi thì `result` null cũng "pass" một test chỉ
        // xem header — nên khoá luôn việc call ĐI ĐƯỢC tới cùng.
        Assert.NotNull(result);
        AssertTokenSent(handler, "/api/v1/suggest-criteria");
    }

    [Fact]
    public async Task QuestionGenerator_dinh_X_Internal_Token()
    {
        var handler = new CapturingHandler("""{"questions":["Câu 1"]}""");
        var sut = new AiServiceQuestionGenerator(
            Http(handler), Config(), NullLogger<AiServiceQuestionGenerator>.Instance);

        await sut.GenerateAsync("BE", "JD", null);

        AssertTokenSent(handler, "/api/v1/generate-questions");
    }
}
