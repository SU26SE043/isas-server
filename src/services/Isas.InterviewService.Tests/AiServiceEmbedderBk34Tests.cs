using System.Net;
using System.Text;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// BK34 — AIService /embed trả non-2xx: `detail` phải đi NGUYÊN VĂN vào AiServiceException. Trước bản này
/// message chỉ còn "AIService /embed trả 502", nên trần 100 request/lô của Gemini (nguyên nhân thật khiến
/// Atlassian/Agile Alliance/Camunda nạp thất bại) bị chẩn đoán nhầm thành "chunk quá cỡ".
/// Mutation: bỏ `+ detail` khỏi message → ĐỎ.
/// </summary>
public class AiServiceEmbedderBk34Tests
{
    private sealed class StubHandler(HttpStatusCode status, string body, string contentType = "application/json") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
    }

    private static AiServiceEmbedder Make(HttpStatusCode status, string body, string contentType = "application/json")
    {
        var http = new HttpClient(new StubHandler(status, body, contentType)) { BaseAddress = new Uri("http://aiapi:8000") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "t" }).Build();
        return new AiServiceEmbedder(http, config, NullLogger<AiServiceEmbedder>.Instance);
    }

    [Fact]
    public async Task NonSuccess_GiuNguyenVanDetailCuaAiService()
    {
        var emb = Make(HttpStatusCode.BadGateway,
            """{"detail":"Lỗi sinh embedding: 400 INVALID_ARGUMENT. BatchEmbedContentsRequest.requests: at most 100 requests can be in one batch"}""");

        var ex = await Assert.ThrowsAsync<AiServiceException>(() => emb.EmbedAsync(["a", "b"], "RETRIEVAL_DOCUMENT"));

        Assert.Contains("502", ex.Message);
        Assert.Contains("at most 100 requests can be in one batch", ex.Message);
    }

    [Fact]
    public async Task NonSuccess_BodyKhongPhaiJson_GiuNguyenVanCatNgan()
    {
        var emb = Make(HttpStatusCode.ServiceUnavailable, new string('x', 1000), "text/plain");

        var ex = await Assert.ThrowsAsync<AiServiceException>(() => emb.EmbedAsync(["a"], "RETRIEVAL_QUERY"));

        Assert.Contains("503", ex.Message);
        Assert.Contains(new string('x', 300), ex.Message);
        Assert.DoesNotContain(new string('x', 301), ex.Message);
    }

    [Fact]
    public async Task NonSuccess_BodyRong_ChiCoMaTrangThai()
    {
        var emb = Make(HttpStatusCode.BadGateway, "");
        var ex = await Assert.ThrowsAsync<AiServiceException>(() => emb.EmbedAsync(["a"], "RETRIEVAL_QUERY"));
        Assert.Equal("AIService /embed trả 502", ex.Message);
    }
}
