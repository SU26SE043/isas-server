using System.Net;
using System.Text;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>B2C coaching (2026-09-17) — client gọi AIService <c>/face-detect</c> (đếm mặt, detect-only).</summary>
public class AiServiceFaceDetectorTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return await respond(request);
        }
    }

    private static AiServiceFaceDetector Client(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "secret",
        }).Build();
        return new AiServiceFaceDetector(http, config, NullLogger<AiServiceFaceDetector>.Instance);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GoiDungDuong_KemKhoaVaToken_DocDuResponse()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"faceCount":1,"signals":[]}""")));
        var client = Client(handler);

        var result = await client.DetectAsync("practice-face/abc/def/xyz.jpg");

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://aiapi:8000/api/v1/face-detect", req.RequestUri!.ToString());
        Assert.Equal("secret", Assert.Single(req.Headers.GetValues("X-Internal-Token")));
        Assert.Contains("\"imageKey\":\"practice-face/abc/def/xyz.jpg\"", handler.Bodies[0]);

        Assert.Equal(1, result.FaceCount);
        Assert.Empty(result.Signals);
    }

    [Fact]
    public async Task ZeroMat_TraNoFace()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"faceCount":0,"signals":["no_face"]}""")));
        var result = await Client(handler).DetectAsync("k.jpg");

        Assert.Equal(0, result.FaceCount);
        Assert.Equal(new[] { "no_face" }, result.Signals);
    }

    [Fact]
    public async Task NhieuMat_TraMultipleFaces()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"faceCount":3,"signals":["multiple_faces"]}""")));
        var result = await Client(handler).DetectAsync("k.jpg");

        Assert.Equal(3, result.FaceCount);
        Assert.Equal(new[] { "multiple_faces" }, result.Signals);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonSuccess_NemAiServiceException(HttpStatusCode code)
    {
        var handler = new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(code) { Content = new StringContent("lỗi từ AIService") }));

        var ex = await Assert.ThrowsAsync<AiServiceException>(() => Client(handler).DetectAsync("k.jpg"));
        Assert.Contains(((int)code).ToString(), ex.Message);
        Assert.False(ex.IsTimeout);
    }

    [Fact]
    public async Task Timeout_NemAiServiceException_IsTimeoutTrue()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("hết giờ"));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "secret",
        }).Build();
        var client = new AiServiceFaceDetector(http, config, NullLogger<AiServiceFaceDetector>.Instance);

        var ex = await Assert.ThrowsAsync<AiServiceException>(() => client.DetectAsync("k.jpg"));
        Assert.True(ex.IsTimeout);
    }
}
