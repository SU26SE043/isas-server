using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// `currentLevel` — trình độ HIỆN TẠI suy từ CV — phải đi hết đường AIService → DTO → entity, và
/// phải xuống được prompt roadmap bằng KHOÁ RIÊNG.
///
/// <para>🔴 Vì sao file này tồn tại: trước nó, response của <c>/analyze-cv</c> KHÔNG có wire-test
/// nào. <c>CvAnalysisWireContractTests</c> chỉ phủ chiều FE → .NET; mọi test khác mock thẳng
/// <see cref="IAiServiceCvAnalyzer"/> nên bypass hoàn toàn lớp deserialize. Đó đúng là lớp bug
/// "khoá JSON lệch tên ⇒ nuốt im lặng" đã cắn repo 4 lần (`focusCriteria` · `metricsVersion` ·
/// `adaptiveMaxQuestions` · `grounding`) — và đường này hở toàn bộ.</para>
///
/// <para>Đo qua stub <c>HttpMessageHandler</c> vì lỗi đánh rơi field nằm ở tầng deserialize/map,
/// không phải ở logic service — cùng khuôn với <c>JdQuoteMappingTests</c>.</para>
/// </summary>
public class CvCurrentLevelWireTests
{
    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private static AiServiceCvAnalyzer Analyzer(string body) => new(
        new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://ai.test") },
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Internal:Token"] = "tok" }).Build(),
        NullLogger<AiServiceCvAnalyzer>.Instance);

    [Fact]
    public async Task CurrentLevel_DiHetDuongTuAiServiceSangDto()
    {
        var sut = Analyzer("""
            {"summary":"Tóm tắt","strengths":[],"weaknesses":[],"suggestions":[],
             "currentLevel":"Middle"}
            """);

        var result = await sut.AnalyzeAsync("cv", null, "BE", default);

        Assert.Equal("Middle", result.CurrentLevel);
    }

    [Fact]
    public async Task VangKhoa_ThiLaNull_KhongPhaiLoi()
    {
        // AIService khai `response_model_exclude_none=True` ⇒ khi không đủ căn cứ thì khoá bị XOÁ
        // khỏi JSON chứ không trả `null`. Phía .NET phải coi đó là "không biết", không phải lỗi.
        var sut = Analyzer("""
            {"summary":"Tóm tắt","strengths":[],"weaknesses":[],"suggestions":[]}
            """);

        var result = await sut.AnalyzeAsync("cv", null, "BE", default);

        Assert.Null(result.CurrentLevel);
    }

    [Fact]
    public async Task NullTuongMinh_CungLaNull()
    {
        var sut = Analyzer("""
            {"summary":"Tóm tắt","strengths":[],"weaknesses":[],"suggestions":[],
             "currentLevel":null}
            """);

        var result = await sut.AnalyzeAsync("cv", null, "BE", default);

        Assert.Null(result.CurrentLevel);
    }
}

/// <summary>
/// Payload gửi xuống <c>/generate-roadmap</c>: `currentLevel` phải có mặt bằng KHOÁ RIÊNG, và
/// `cvText` phải BIẾN MẤT.
/// </summary>
public class RoadmapCurrentLevelPayloadTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"milestones":[{"title":"M","focusCriteria":[],"lessons":[{"title":"L"}]}]}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private static (AiServiceRoadmapGenerator Gen, CaptureHandler Handler) Generator()
    {
        var handler = new CaptureHandler();
        var gen = new AiServiceRoadmapGenerator(
            new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") },
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Internal:Token"] = "tok" }).Build(),
            NullLogger<AiServiceRoadmapGenerator>.Instance);
        return (gen, handler);
    }

    [Fact]
    public async Task Payload_CoKhoaCurrentLevel_DungTen()
    {
        // ⚠ Lệch tên khoá là lớp bug đã cắn repo 4 lần: AIService khai `extra='ignore'` nên field
        // sai tên bị NUỐT IM LẶNG — roadmap vẫn sinh, không lỗi, chỉ là mất sàn trình độ.
        var (gen, handler) = Generator();

        await gen.GenerateAsync("BE", "Senior", null, null, null, null, currentLevel: "Junior");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.True(doc.RootElement.TryGetProperty("currentLevel", out var v),
            "payload /generate-roadmap phải có khoá 'currentLevel' — đúng tên app.schemas đọc");
        Assert.Equal("Junior", v.GetString());
    }

    [Fact]
    public async Task Payload_KHONG_CON_CvText()
    {
        // CV thô đã bị gỡ khỏi luồng roadmap. Đo trên production trước khi gỡ: roadmap có CV và
        // không CV cho tên chặng không phân biệt được, nhóm có CV còn nêu công nghệ cụ thể ÍT hơn
        // (8,6% vs 12,1% số bài). Test này chặn việc nối lại theo phản xạ.
        var (gen, handler) = Generator();

        await gen.GenerateAsync("BE", "Senior", null, null, "Tóm tắt CV", null);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.False(doc.RootElement.TryGetProperty("cvText", out _),
            "payload không được mang CV thô nữa");
        // Đường thay thế vẫn phải còn — gỡ CV thô không được kéo theo nó.
        Assert.True(doc.RootElement.TryGetProperty("cvAnalysisSummary", out var s));
        Assert.Equal("Tóm tắt CV", s.GetString());
    }

    [Fact]
    public async Task KhongTruyen_ThiKhoaVanCoMatVoiGiaTriNull()
    {
        // Giữ khoá với giá trị null (thay vì bỏ hẳn) để phía Python luôn thấy một hợp đồng ổn định;
        // `currentLevel: str | None = None` nhận null bình thường.
        var (gen, handler) = Generator();

        await gen.GenerateAsync("BE", "Senior", null, null, null, null);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.True(doc.RootElement.TryGetProperty("currentLevel", out var v));
        Assert.Equal(JsonValueKind.Null, v.ValueKind);
    }
}
