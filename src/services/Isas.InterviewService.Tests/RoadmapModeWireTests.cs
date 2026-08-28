using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// HỢP ĐỒNG DÂY cho <c>mode</c> giữa InterviewService và <c>/generate-roadmap</c> +
/// <c>/generate-lesson-theory</c>.
///
/// <para>Mẫu <see cref="RoadmapCriteriaWireBe1Tests"/>, và vì đúng lý do đã ghi ở đó: đổi TÊN
/// trường JSON KHÔNG ném lỗi ở đâu cả — pydantic <c>extra='ignore'</c> nuốt im lặng, nên .NET
/// vẫn gửi 200 OK còn AIService sinh lộ trình ở chế độ mặc định. Người dùng thấy lộ trình của
/// mình ghi là "ôn tập" mà nội dung là lộ trình tiến-lên. Repo đã dính lớp lỗi này <b>bốn lần</b>
/// (<c>focusCriteria</c>/BC14 · <c>metricsVersion</c> · <c>adaptiveMaxQuestions</c> ·
/// <c>grounding</c>).</para>
///
/// <para>⚠ Phép đối chứng kiểu "không có khoá PascalCase" là VÔ NGHĨA: <c>JsonContent.Create</c>
/// dùng <c>JsonSerializerDefaults.Web</c> nên luôn ra camelCase. Phép có nghĩa là khoá đúng TÊN
/// và đúng GIÁ TRỊ chuỗi mà <c>app.roadmap_mode</c> chấp nhận.</para>
/// </summary>
public class RoadmapModeWireTests
{
    private sealed class CaptureHandler(string json) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (AiServiceRoadmapGenerator gen, CaptureHandler handler) Generator(string json)
    {
        var handler = new CaptureHandler(json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000") };
        return (new AiServiceRoadmapGenerator(
            http, new ConfigurationBuilder().Build(),
            NullLogger<AiServiceRoadmapGenerator>.Instance), handler);
    }

    private const string RoadmapJson =
        """{"milestones":[{"title":"M1","focusCriteria":[],"lessons":[{"title":"L1"}]}]}""";
    private const string LessonJson =
        """{"theoryMarkdown":"## T\n\nNội dung","resources":[]}""";

    [Theory]
    [InlineData(RoadmapMode.Reinforce, "Reinforce")]
    [InlineData(RoadmapMode.LevelUp, "LevelUp")]
    public async Task Wire_GenerateRoadmap_GuiDungTenKhoaVaGiaTriMode(
        RoadmapMode mode, string expected)
    {
        var (gen, handler) = Generator(RoadmapJson);
        await gen.GenerateAsync("BA", "Junior", null, null,
            criteria: null, mode: mode);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.True(doc.RootElement.TryGetProperty("mode", out var value),
            "payload /generate-roadmap phải có khoá 'mode' — đúng tên app.roadmap_mode đọc");
        Assert.Equal(expected, value.GetString());
    }

    [Theory]
    [InlineData(RoadmapMode.Reinforce, "Reinforce")]
    [InlineData(RoadmapMode.LevelUp, "LevelUp")]
    public async Task Wire_GenerateLessonTheory_GuiDungTenKhoaVaGiaTriMode(
        RoadmapMode mode, string expected)
    {
        var (gen, handler) = Generator(LessonJson);
        await gen.GenerateLessonTheoryAsync(
            "BA", "Junior", "Bài 1", [], null,
            grounding: null, evidence: null, mode: mode);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.True(doc.RootElement.TryGetProperty("mode", out var value),
            "payload /generate-lesson-theory phải có khoá 'mode'");
        Assert.Equal(expected, value.GetString());
    }

    /// <summary>
    /// Giá trị gửi đi phải nằm trong tập <c>app.roadmap_mode._MODES</c>. Gửi một chuỗi ngoài tập
    /// đó không gây lỗi ở đâu cả — <c>normalize_mode</c> fail-open nên nó âm thầm thành LevelUp.
    /// </summary>
    [Fact]
    public async Task Wire_GiaTriGuiDi_ThuocTapAIServiceChapNhan()
    {
        foreach (var mode in Enum.GetValues<RoadmapMode>())
        {
            var (gen, handler) = Generator(RoadmapJson);
            await gen.GenerateAsync("BA", "Junior", null, null,
                criteria: null, mode: mode);
            using var doc = JsonDocument.Parse(handler.LastBody!);
            var sent = doc.RootElement.GetProperty("mode").GetString();
            Assert.Contains(sent, new[] { "LevelUp", "Reinforce" });
        }
    }
}
