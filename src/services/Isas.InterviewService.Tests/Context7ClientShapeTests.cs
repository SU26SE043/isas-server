using System.Net;
using System.Text;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Isas.InterviewService.Tests;

// RAG grounding — KHÓA shape Context7 v2 THẬT (supervisor verify bằng call keyless 2026-08-01).
// Stub HttpMessageHandler trả JSON THẬT đã bắt + capture RequestUri → chứng minh:
//   (1) URL đúng "/api/v2/libs/search" & "/api/v2/context" (bug leading-slash mất /api/v2 đã bịt),
//   (2) param đúng libraryId&query (không phải library/topic),
//   (3) parse codeSnippets + infoSnippets ra content + sourceUrl từ codeId/pageId.
// Đây là verify duy nhất chạm shape thật (unit test khác mock IContext7Client).
public class Context7ClientShapeTests
{
    private sealed class CaptureHandler(string json) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (Context7Client client, CaptureHandler handler) Make(string json)
    {
        var handler = new CaptureHandler(json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://context7.com/api/v2/") };
        var config = new ConfigurationBuilder().Build();
        return (new Context7Client(http, config, NullLogger<Context7Client>.Instance), handler);
    }

    // Trích từ call THẬT /libs/search?libraryName=react&query=hooks (rút gọn, giữ nguyên tên field).
    private const string SearchJson = """
    {"results":[{"id":"/reactjs/react.dev","title":"React","description":"official docs","branch":"main","state":"finalized","totalTokens":689822,"totalSnippets":6240,"stars":11311,"trustScore":10,"benchmarkScore":88.19,"versions":["__branch__v18"]},{"id":"/react/react","title":"React","description":"lib","totalSnippets":6185,"trustScore":8.3}],"searchFilterApplied":false}
    """;

    // Trích từ call THẬT /context?libraryId=/reactjs/react.dev&query=useEffect&type=json.
    private const string ContextJson = """
    {"codeSnippets":[{"codeTitle":"Declaring an Effect","codeDescription":"connect and disconnect from a chat room.","codeLanguage":"js","codeId":"https://github.com/reactjs/react.dev/blob/main/src/content/reference/react/useEffect.md","pageTitle":"useEffect","codeList":[{"language":"js","code":"useEffect(() => {}, []);"}]},{"codeTitle":"Basic useEffect signature","codeDescription":"signature","codeId":"https://github.com/reactjs/react.dev/blob/main/src/content/reference/react/useEffect.md","pageTitle":"useEffect","codeList":[{"language":"js","code":"useEffect(setup, dependencies?)"}]}],"infoSnippets":[{"pageId":"https://github.com/reactjs/react.dev/blob/main/src/content/reference/react/useEffect.md","breadcrumb":"useEffect","content":"useEffect is a React Hook that lets you synchronize a component with an external system.","contentTokens":19}]}
    """;

    [Fact]
    public async Task Search_RealV2Shape_CorrectUrl_ParsesLibraries()
    {
        var (client, handler) = Make(SearchJson);
        var libs = await client.SearchAsync("react", "hooks");

        Assert.NotNull(handler.LastUri);
        Assert.Equal("/api/v2/libs/search", handler.LastUri!.AbsolutePath); // /api/v2 KHÔNG bị mất
        Assert.Contains("libraryName=react", handler.LastUri.Query);
        Assert.Contains("query=hooks", handler.LastUri.Query);

        Assert.Equal(2, libs.Count);
        Assert.Equal("/reactjs/react.dev", libs[0].Id);
        Assert.Equal("React", libs[0].Title);
        Assert.Equal("10", libs[0].Reputation); // trustScore (field THẬT, không phải "reputation")
        Assert.Equal(6240, libs[0].Snippets);    // totalSnippets
    }

    [Fact]
    public async Task GetContext_RealV2Shape_CorrectUrl_ParsesCodeAndInfoSnippets_WithSourceUrl()
    {
        var (client, handler) = Make(ContextJson);
        var snippets = await client.GetContextAsync("/reactjs/react.dev", "useEffect");

        Assert.NotNull(handler.LastUri);
        Assert.Equal("/api/v2/context", handler.LastUri!.AbsolutePath);
        Assert.Contains("libraryId=", handler.LastUri.Query); // param THẬT là libraryId, không phải library
        Assert.Contains("query=useEffect", handler.LastUri.Query); // param THẬT là query, không phải topic

        Assert.Equal(3, snippets.Count); // 2 codeSnippets + 1 infoSnippet
        Assert.All(snippets, s => Assert.False(string.IsNullOrWhiteSpace(s.Content)));
        Assert.All(snippets, s => Assert.Contains("github.com", s.SourceUrl!)); // codeId/pageId → sourceUrl
        Assert.Contains(snippets, s => s.Title == "Declaring an Effect"); // codeTitle
        Assert.Contains(snippets, s => s.Content.Contains("synchronize")); // infoSnippet content
    }
}
