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
/// REC1-B7 — 🔴 TIỀN ĐỀ ĐÃ ĐẢO so với bản gốc (từng khoá "currentLevel/cvAnalysisSummary PHẢI có
/// mặt trên dây"): payload gửi xuống <c>/generate-roadmap</c> nay PHẢI SẠCH cả ba khoá
/// `cvAnalysisSummary`/`priorRoadmapSummary`/`currentLevel` — không chỉ `cvText` thô (đã gỡ TRƯỚC
/// bước này, MIS1-B5). Lý do đảo: prompt roadmap chỉ xuất ra CẤU TRÚC, mà cả hai nguồn CV/lộ trình
/// trước đều bị chèn kèm câu "không đổi cấu trúc roadmap" — mệnh lệnh tự phủ định. Đo được: nhóm
/// CÓ chọn CV nêu công nghệ cụ thể ÍT hơn (8,6% vs 12,1%); lộ trình trước chỉ 4/37 đủ điều kiện
/// trên dev, 0 trên môi trường chính.
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
    public async Task Payload_KHONG_CON_KhoaCurrentLevel()
    {
        // 🔴 REC1-B7 — CẤM: gán `currentLevel = null` để "bỏ" field KHÔNG đủ (JsonContent.Create
        // dùng JsonSerializerDefaults.Web, DefaultIgnoreCondition = Never ⇒ vẫn ra "currentLevel":
        // null trên dây). Tham số `currentLevel` đã GỠ HẲN khỏi chữ ký GenerateAsync — không còn
        // cách nào truyền nó nữa, nên chỉ cần gọi bình thường rồi khẳng định khoá vắng mặt.
        var (gen, handler) = Generator();

        await gen.GenerateAsync("BE", "Senior", null, null);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.False(doc.RootElement.TryGetProperty("currentLevel", out _),
            "payload /generate-roadmap KHÔNG được mang khoá 'currentLevel' nữa");
    }

    [Fact]
    public async Task Payload_KHONG_CON_CvText_LanCvAnalysisSummary()
    {
        // CV thô đã bị gỡ khỏi luồng roadmap TRƯỚC bước này (MIS1-B5). REC1-B7 gỡ nốt đường thay
        // thế `cvAnalysisSummary` — đo trên production: roadmap có CV và không CV cho tên chặng
        // KHÔNG phân biệt được, nhóm có CV còn nêu công nghệ cụ thể ÍT hơn (8,6% vs 12,1% số bài).
        var (gen, handler) = Generator();

        await gen.GenerateAsync("BE", "Senior", null, null);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.False(doc.RootElement.TryGetProperty("cvText", out _),
            "payload không được mang CV thô");
        Assert.False(doc.RootElement.TryGetProperty("cvAnalysisSummary", out _),
            "payload không được mang tóm tắt CV nữa — REC1-B7 gỡ luôn đường thay thế");
    }

    [Fact]
    public async Task Payload_KHONG_CON_KhoaPriorRoadmapSummary()
    {
        var (gen, handler) = Generator();

        await gen.GenerateAsync("BE", "Senior", null, null);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.False(doc.RootElement.TryGetProperty("priorRoadmapSummary", out _),
            "payload không được mang tóm tắt roadmap trước nữa");
    }
}
