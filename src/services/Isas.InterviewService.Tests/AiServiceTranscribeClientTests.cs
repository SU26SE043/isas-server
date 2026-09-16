using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Client chép lời cho màn "tự thử thước đo" (admin nói vào mic → chép lời + số đo → chấm bằng bộ
/// chấm thật). Đây là cầu DUY NHẤT tới AIService <c>/transcribe</c> (gate token, không qua gateway).
/// </summary>
public class AiServiceTranscribeClientTests
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

    private static AiServiceTranscribeClient Client(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "secret",
        }).Build();
        return new AiServiceTranscribeClient(http, config, NullLogger<AiServiceTranscribeClient>.Instance);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string OkBody = """
        {"text":"Em sẽ thêm index cho cột hay lọc.",
         "deliveryMetrics":{"metricsVersion":2,"audioSec":48.0,"speechSec":42.0,"wordCount":110,
                            "speechRateWpm":157.0,"longestPauseSec":1.8,"pauseCount":3,"silenceRatio":0.12,
                            "fillerCount":1,"fillerPer100Words":0.9,"fillerBreakdown":{"ừm":1}},
         "transcriptEngine":"whisper-1","rejectReason":null}
        """;

    [Fact]
    public async Task GoiDungDuong_Multipart_KemToken_VaDocDuResponse()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(OkBody)));
        var client = Client(handler);

        using var audio = new MemoryStream(Encoding.ASCII.GetBytes("RIFF....fake-audio"));
        var result = await client.TranscribeAsync(audio, "answer.webm", "audio/webm", "vi");

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://aiapi:8000/api/v1/transcribe?language=vi", req.RequestUri!.ToString());
        Assert.Equal("secret", Assert.Single(req.Headers.GetValues("X-Internal-Token")));
        Assert.StartsWith("multipart/form-data", req.Content!.Headers.ContentType!.ToString());
        Assert.Contains("name=file", handler.Bodies[0]);           // đúng tên trường FastAPI đợi
        Assert.Contains("filename=answer.webm", handler.Bodies[0]);
        Assert.Contains("fake-audio", handler.Bodies[0]);

        Assert.Equal("Em sẽ thêm index cho cột hay lọc.", result.Text);
        Assert.Equal("whisper-1", result.TranscriptEngine);
        Assert.Null(result.RejectReason);
        Assert.NotNull(result.DeliveryMetrics);
        Assert.Equal(0.12, result.DeliveryMetrics!.SilenceRatio);
        Assert.Equal(3, result.DeliveryMetrics.PauseCount);
        Assert.Equal(2, result.DeliveryMetrics.MetricsVersion);
    }

    [Fact]
    public async Task KhongCoTiengNoi_TraRejectReason_KhongNem()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(
            """{"text":"","deliveryMetrics":null,"transcriptEngine":null,"rejectReason":"no_speech"}""")));
        var result = await Client(handler).TranscribeAsync(new MemoryStream([1, 2, 3]), "a.webm", "audio/webm", "vi");
        Assert.Equal("", result.Text);
        Assert.Equal("no_speech", result.RejectReason);
        Assert.Null(result.DeliveryMetrics);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AIServiceLoi_NemDownstream(HttpStatusCode code)
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"detail":"hỏng"}""", code)));
        await Assert.ThrowsAsync<DownstreamServiceException>(
            () => Client(handler).TranscribeAsync(new MemoryStream([1]), "a.webm", "audio/webm", "vi"));
    }

    [Fact]
    public async Task MatMang_NemDownstream()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<DownstreamServiceException>(
            () => Client(handler).TranscribeAsync(new MemoryStream([1]), "a.webm", "audio/webm", "vi"));
    }

    /// <summary>
    /// Hợp đồng chéo: 4 khoá JSON mà `main.py::transcribe` trả PHẢI là 4 khoá client đọc. Đổi tên một bên
    /// mà quên bên kia thì field rụng im lặng (lớp bug `focusCriteria`/`metricsVersion`) — đọc thẳng file Python.
    /// </summary>
    [Fact]
    public void KhoaJson_KhopVoiMainPy()
    {
        var mainPy = File.ReadAllText(Path.Combine(RepoRoot(), "src", "services", "Isas.AIService", "app", "main.py"));
        var i = mainPy.IndexOf("@router.post(\"/transcribe\")", StringComparison.Ordinal);
        Assert.True(i > 0, "không tìm thấy endpoint /transcribe trong main.py");
        var block = mainPy.Substring(i, Math.Min(2500, mainPy.Length - i));
        foreach (var key in new[] { "\"text\":", "\"deliveryMetrics\":", "\"transcriptEngine\":", "\"rejectReason\":" })
            Assert.Contains(key, block);
    }

    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
}
