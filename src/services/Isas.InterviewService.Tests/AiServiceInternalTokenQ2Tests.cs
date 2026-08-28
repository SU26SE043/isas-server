using System.Net;
using System.Text;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Q2/GEN-7 — mọi client Interview gọi AIService phải đính <c>X-Internal-Token</c>.
///
/// Vì sao khoá bằng test: AIService nay gate 8 endpoint SINH (fail-closed). Client quên header thì
/// KHÔNG hỏng lúc build, chỉ hỏng lúc chạy thật — và hỏng theo kiểu khó thấy nhất: phân tích CV ra
/// 502, nhận xét buổi luyện im lặng thành null (caller best-effort nuốt lỗi). Trước Q2, đường
/// /generate-questions của Interview đã đính token còn 4 đường này thì chưa; test này chặn việc
/// bất đối xứng đó tái diễn.
///
/// Đo bằng cách bắt request outbound bằng stub handler — cùng khuôn
/// <c>Isas.CampaignService.Tests/AiServiceFaceVerifyClientTokenTests.cs</c>.
/// </summary>
public class AiServiceInternalTokenQ2Tests
{
    private const string Token = "tok-interview-q2";

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

    /// <summary>Header có mặt, đúng giá trị config, và gửi tới đúng đường dẫn.</summary>
    private static void AssertTokenSent(CapturingHandler handler, string expectedPath)
    {
        Assert.NotNull(handler.Last);
        Assert.Equal(expectedPath, handler.Last!.RequestUri?.AbsolutePath);
        Assert.True(handler.Last.Headers.TryGetValues("X-Internal-Token", out var values),
            $"Thiếu X-Internal-Token khi gọi {expectedPath} — AIService gate fail-closed sẽ trả 401.");
        Assert.Equal(Token, Assert.Single(values!));
    }

    [Fact]
    public async Task CvAnalyzer_dinh_X_Internal_Token()
    {
        var handler = new CapturingHandler(
            """{"summary":"s","strengths":[],"weaknesses":[],"suggestions":[]}""");
        var sut = new AiServiceCvAnalyzer(Http(handler), Config(), NullLogger<AiServiceCvAnalyzer>.Instance);

        await sut.AnalyzeAsync("BE", "cv text", null);

        AssertTokenSent(handler, "/api/v1/analyze-cv");
    }

    [Fact]
    public async Task CvAnalyzer_SuggestJdRequirements_dinh_X_Internal_Token()
    {
        var handler = new CapturingHandler(
            """{"mustHave":[{"text":"Docker","citations":[]}],"niceToHave":[]}""");
        var sut = new AiServiceCvAnalyzer(Http(handler), Config(), NullLogger<AiServiceCvAnalyzer>.Instance);

        await sut.SuggestJdRequirementsAsync("BE", "Need Docker", []);

        AssertTokenSent(handler, "/api/v1/suggest-jd-requirements");
    }

    [Fact]
    public async Task RoadmapGenerator_GenerateAsync_dinh_X_Internal_Token()
    {
        var handler = new CapturingHandler(
            """{"milestones":[{"title":"M1","focusCriteria":["A"],"lessons":[{"title":"L1"}]}]}""");
        var sut = new AiServiceRoadmapGenerator(Http(handler), Config(), NullLogger<AiServiceRoadmapGenerator>.Instance);

        await sut.GenerateAsync("BE", "Junior", null, null, null);

        AssertTokenSent(handler, "/api/v1/generate-roadmap");
    }

    [Fact]
    public async Task RoadmapGenerator_GenerateLessonTheoryAsync_dinh_X_Internal_Token()
    {
        var handler = new CapturingHandler("""{"theoryMarkdown":"# Bài\n\nNội dung"}""");
        var sut = new AiServiceRoadmapGenerator(Http(handler), Config(), NullLogger<AiServiceRoadmapGenerator>.Instance);

        await sut.GenerateLessonTheoryAsync("BE", "Junior", "Bài 1", ["Tiêu chí A"], null);

        AssertTokenSent(handler, "/api/v1/generate-lesson-theory");
    }

    [Fact]
    public async Task RoadmapGenerator_SummarizeRoadmapAsync_dinh_X_Internal_Token()
    {
        var handler = new CapturingHandler(
            """{"strengths":[],"weaknesses":[],"improvements":[],"overallComment":"ok"}""");
        var sut = new AiServiceRoadmapGenerator(Http(handler), Config(), NullLogger<AiServiceRoadmapGenerator>.Instance);

        await sut.SummarizeRoadmapAsync("BE", "Junior", []);

        AssertTokenSent(handler, "/api/v1/summarize-roadmap");
    }

    [Fact]
    public async Task SessionSummarizer_dinh_X_Internal_Token()
    {
        var handler = new CapturingHandler("""{"overallComment":"nhận xét"}""");
        var sut = new AiServiceSessionSummarizer(
            Http(handler), Config(), NullLogger<AiServiceSessionSummarizer>.Instance);

        await sut.SummarizeAsync("BE", 80m, [new SessionSummaryCriterion("A", 80m, false)]);

        AssertTokenSent(handler, "/api/v1/summarize-session");
    }
}
