using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// MIS1-B5 — HỢP ĐỒNG DÂY của payload <c>/generate-roadmap</c> và <c>/generate-lesson-theory</c>
/// (lớp <see cref="AiServiceRoadmapGenerator"/>) sau khi nối lỗi sai (MIS1-B4) vào và gỡ hẳn
/// <c>evidence</c> (MIS1-B2 đã bỏ chế độ giáo trình dùng nó).
///
/// <para>Mẫu <c>HttpMessageHandler</c> bắt raw body — cùng kỹ thuật <see cref="RoadmapCriteriaWireBe1Tests"/>/
/// <see cref="SeniorityWireSen1Tests"/> — vì kiểm ở tầng mock-interface bỏ qua hẳn bước
/// <c>JsonContent.Create</c> serialize thật, không chứng minh được tên khoá/hình dạng ra dây.</para>
/// </summary>
public class RoadmapMistakePayloadMis1B5Tests
{
    private sealed class CaptureHandler(string json) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (AiServiceRoadmapGenerator gen, CaptureHandler handler) Generator(string responseJson)
    {
        var handler = new CaptureHandler(responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000") };
        return (new AiServiceRoadmapGenerator(
            http, new ConfigurationBuilder().Build(),
            NullLogger<AiServiceRoadmapGenerator>.Instance), handler);
    }

    private static RoadmapMistake Mistake(string key = "m1") => new()
    {
        Id = Guid.NewGuid(),
        RoadmapId = Guid.NewGuid(),
        MistakeKey = key,
        CriterionId = Guid.NewGuid(),
        CriterionName = "Clarity",
        Question = "Giải thích dependency injection?",
        // Đáp án + đáp án mẫu BÍ MẬT — /generate-roadmap chỉ cần đủ để GOM CHỦ ĐỀ (MIS1-B2), không
        // cần nguyên văn câu trả lời/đáp án mẫu. Nếu payload lỡ tay chiếu 2 trường này ra dây thì
        // bài kiểm dưới đây phải bắt được ngay.
        Answer = "ĐÁP ÁN BÍ MẬT CỦA ỨNG VIÊN — KHÔNG ĐƯỢC RA DÂY /generate-roadmap",
        Reasoning = "Không phân biệt được DI với Service Locator",
        SampleAnswer = "ĐÁP ÁN MẪU BÍ MẬT — KHÔNG ĐƯỢC RA DÂY /generate-roadmap",
        ScorePct = 30m,
        ThresholdPct = 50m,
        CreatedAt = DateTime.UtcNow,
    };

    // ═══════════ Test 4 — /generate-roadmap: mistakes[] KHÔNG có answer/sampleAnswer ═══════════

    [Fact]
    public async Task GenerateRoadmap_MistakesKhongCoAnswerVaSampleAnswer()
    {
        var (gen, handler) = Generator("""{"milestones":[{"title":"M1","focusCriteria":[],"lessons":[{"title":"L1"}]}]}""");

        await gen.GenerateAsync(
            "BE", "Junior", weaknesses: null, focus: null, cvAnalysisSummary: null, priorRoadmapSummary: null,
            mistakes: [Mistake()]);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var m0 = doc.RootElement.GetProperty("mistakes").EnumerateArray().Single();

        // Đủ 5 trường GOM CHỦ ĐỀ: id (mistake_key, để filter_milestone_mistakes phía AIService lọc
        // CHÍNH XÁC) + criterionName + scorePct + question + reasoning.
        Assert.Equal("m1", m0.GetProperty("id").GetString());
        Assert.Equal("Clarity", m0.GetProperty("criterionName").GetString());
        Assert.Equal(30, m0.GetProperty("scorePct").GetInt32());
        Assert.Equal("Giải thích dependency injection?", m0.GetProperty("question").GetString());
        Assert.Contains("Service Locator", m0.GetProperty("reasoning").GetString());

        // 🔒 CẤM tuyệt đối: answer/sampleAnswer KHÔNG được có mặt, dù RoadmapMistake mang giá trị.
        Assert.False(m0.TryGetProperty("answer", out _));
        Assert.False(m0.TryGetProperty("sampleAnswer", out _));
    }

    /// <summary>Trần độ dài — model đã đo phân vị 90 trên production (260/350 ký tự), không phỏng đoán.</summary>
    [Fact]
    public async Task GenerateRoadmap_CatTranDoDaiQuestionVaReasoning()
    {
        var (gen, handler) = Generator("""{"milestones":[{"title":"M1","focusCriteria":[],"lessons":[{"title":"L1"}]}]}""");
        var longMistake = Mistake();
        longMistake.Question = new string('q', 300);
        longMistake.Reasoning = new string('r', 400);

        await gen.GenerateAsync(
            "BE", "Junior", weaknesses: null, focus: null, cvAnalysisSummary: null, priorRoadmapSummary: null,
            mistakes: [longMistake]);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var m0 = doc.RootElement.GetProperty("mistakes").EnumerateArray().Single();
        Assert.Equal(260, m0.GetProperty("question").GetString()!.Length);
        Assert.Equal(350, m0.GetProperty("reasoning").GetString()!.Length);
    }

    // ═══════════ Test 6 — payload KHÔNG còn khoá `evidence`, và không ném ═══════════

    /// <summary>
    /// 🔒 <c>evidence</c> đã gỡ khỏi CẢ HAI payload (MIS1-B2 bỏ chế độ giáo trình dùng nó). Vì
    /// <c>JsonContent.Create</c> dùng <c>JsonSerializerDefaults.Web</c>
    /// (<c>DefaultIgnoreCondition = Never</c>), CHỈ "không truyền dữ liệu" (evidence luôn null) là
    /// KHÔNG ĐỦ — property còn khai trong anonymous object vẫn ra <c>"evidence":null</c> trên dây.
    /// Phải xoá HẲN property đó. Tham số hàm <c>evidence</c> vẫn giữ nguyên chữ ký (interface/call
    /// site cũ không vỡ) — chỉ không còn được CHIẾU vào payload.
    /// </summary>
    [Fact]
    public async Task GenerateRoadmap_PayloadKhongConKhoaEvidence_KhongNem()
    {
        var (gen, handler) = Generator("""{"milestones":[{"title":"M1","focusCriteria":[],"lessons":[{"title":"L1"}]}]}""");

        var ex = await Record.ExceptionAsync(() => gen.GenerateAsync(
            "BE", "Junior", weaknesses: null, focus: null, cvAnalysisSummary: null, priorRoadmapSummary: null));
        Assert.Null(ex);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.False(doc.RootElement.TryGetProperty("evidence", out _));
    }

    [Fact]
    public async Task GenerateLessonTheory_PayloadKhongConKhoaEvidence_KhongNem()
    {
        var (gen, handler) = Generator(
            """{"theoryMarkdown":"## Lý thuyết\n\nNội dung","resources":[]}""");

        var ex = await Record.ExceptionAsync(() => gen.GenerateLessonTheoryAsync(
            "BE", "Junior", "Tổng quan OOP", focusCriteria: [], weaknesses: null));
        Assert.Null(ex);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.False(doc.RootElement.TryGetProperty("evidence", out _));
    }

    /// <summary>
    /// Đối chứng: <c>/generate-lesson-theory</c> vẫn gửi ĐỦ 6 trường (kể cả answer/sampleAnswer —
    /// bài giảng cần NGUYÊN VĂN để giải thích sai ở đâu), khác hẳn <c>/generate-roadmap</c> ở trên.
    /// Hai payload khác hình dạng có chủ đích — bài kiểm này khoá đúng sự khác biệt đó, không phải
    /// một mutation "xoá field cho gọn" âm thầm làm chúng giống nhau.
    /// </summary>
    [Fact]
    public async Task GenerateLessonTheory_MistakesCoDuNguyenVanAnswerVaSampleAnswer()
    {
        var (gen, handler) = Generator("""{"theoryMarkdown":"## Lý thuyết\n\nNội dung","resources":[]}""");

        await gen.GenerateLessonTheoryAsync(
            "BE", "Junior", "Tổng quan OOP", focusCriteria: [], weaknesses: null,
            mistakes: [Mistake()]);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var m0 = doc.RootElement.GetProperty("mistakes").EnumerateArray().Single();
        Assert.Equal("ĐÁP ÁN BÍ MẬT CỦA ỨNG VIÊN — KHÔNG ĐƯỢC RA DÂY /generate-roadmap", m0.GetProperty("answer").GetString());
        Assert.Equal("ĐÁP ÁN MẪU BÍ MẬT — KHÔNG ĐƯỢC RA DÂY /generate-roadmap", m0.GetProperty("sampleAnswer").GetString());
    }
}
