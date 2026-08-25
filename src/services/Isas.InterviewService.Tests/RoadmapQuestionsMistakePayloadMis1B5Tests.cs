using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// MIS1-B5 — HỢP ĐỒNG DÂY của <c>lessonContext.mistakes</c> trong payload <c>/generate-questions</c>
/// (<see cref="AiServiceQuestionGenerator"/>). Đây là điểm CẤM nặng nhất của cả B5: đưa đáp án vào
/// prompt SINH CÂU HỎI thì model lấy luôn nội dung đáp án làm câu hỏi — khác hẳn 2 endpoint kia
/// (roadmap/lesson-theory) chỉ cần GOM CHỦ ĐỀ hoặc giải thích lại lỗi.
///
/// <para>Mẫu <c>HttpMessageHandler</c> bắt raw body — cùng kỹ thuật
/// <see cref="LessonContextWireTests"/>/<see cref="SeniorityWireSen1Tests"/>.</para>
/// </summary>
public class RoadmapQuestionsMistakePayloadMis1B5Tests
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

    private static (AiServiceQuestionGenerator gen, CaptureHandler handler) Generator()
    {
        var handler = new CaptureHandler("""{"questions":["Q1"]}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000") };
        return (new AiServiceQuestionGenerator(
            http, new ConfigurationBuilder().Build(),
            NullLogger<AiServiceQuestionGenerator>.Instance), handler);
    }

    // ═══════════ Test 5 — lessonContext.mistakes[] KHÔNG có answer/sampleAnswer/scorePct ═══════════

    /// <summary>
    /// 🔒 CẤM tuyệt đối: <c>RoadmapMistakeWire</c> (kiểu của <c>LessonContext.Mistakes</c>) mang cả
    /// <c>Answer</c>/<c>ScorePct</c> (dùng cho 2 endpoint kia), nhưng payload
    /// <c>/generate-questions</c> chỉ được chiếu ĐÚNG 4 trường: id/criterionName/question/reasoning.
    /// <c>SampleAnswer</c> đã bị loại khỏi CHÍNH KIỂU <c>RoadmapMistakeWire</c> (không có field nào để
    /// mà lỡ tay chiếu ra) — bài kiểm này khoá vế còn lại: <c>Answer</c>/<c>ScorePct</c> tuy CÓ trong
    /// kiểu vẫn không được lọt ra dây.
    /// </summary>
    [Fact]
    public async Task GenerateQuestions_LessonContextMistakes_KhongCoAnswerVaScorePct()
    {
        var (gen, handler) = Generator();
        var mistake = new RoadmapMistakeWire(
            Id: "m1", CriterionName: "Clarity", Question: "Giải thích dependency injection?",
            Reasoning: "Không phân biệt được DI với Service Locator",
            ScorePct: 30m,
            Answer: "ĐÁP ÁN BÍ MẬT CỦA ỨNG VIÊN — KHÔNG ĐƯỢC RA DÂY /generate-questions");

        await gen.GenerateQuestionsAsync(
            "BE", cvText: null, jdText: null, focusCriteria: null, count: 5,
            grounding: null, "vi", criteria: null, "Junior",
            new LessonContext("Tổng quan OOP", "Đóng gói\nKế thừa", [mistake]));

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var m0 = doc.RootElement.GetProperty("lessonContext").GetProperty("mistakes").EnumerateArray().Single();

        Assert.Equal("m1", m0.GetProperty("id").GetString());
        Assert.Equal("Clarity", m0.GetProperty("criterionName").GetString());
        Assert.Equal("Giải thích dependency injection?", m0.GetProperty("question").GetString());
        Assert.Contains("Service Locator", m0.GetProperty("reasoning").GetString());

        Assert.False(m0.TryGetProperty("answer", out _));
        Assert.False(m0.TryGetProperty("sampleAnswer", out _));
        Assert.False(m0.TryGetProperty("scorePct", out _));

        // Đúng 4 trường — không thừa, không thiếu.
        Assert.Equal(4, m0.EnumerateObject().Count());
    }

    /// <summary>Không có lỗi nào để bám (Mistakes null/rỗng) ⇒ KHÔNG mọc khoá <c>mistakes</c> mang nội dung.</summary>
    [Fact]
    public async Task GenerateQuestions_KhongCoMistake_LessonContextKhongMangKhoaMistakesCoNoiDung()
    {
        var (gen, handler) = Generator();

        await gen.GenerateQuestionsAsync(
            "BE", null, null, null, 5, null, "vi", null, "Junior",
            new LessonContext("Tổng quan OOP", "Đóng gói\nKế thừa"));

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var lc = doc.RootElement.GetProperty("lessonContext");
        Assert.True(
            !lc.TryGetProperty("mistakes", out var m) || m.ValueKind == JsonValueKind.Null);
    }
}
