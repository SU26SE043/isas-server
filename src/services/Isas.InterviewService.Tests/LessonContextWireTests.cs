using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Câu hỏi buổi luyện bài học phải bám ĐÚNG BÀI, không phải bám CHẶNG.
///
/// <para><b>Vấn đề đang sống</b> (đo trên dev 2026-08-23): <c>/start</c> chỉ gửi
/// <c>lesson.Milestone.FocusCriteria</c> — tiêu chí của CHẶNG — nên mọi bài trong cùng một chặng
/// cho lớp sinh đúng một đầu vào. Chặng "Nền tảng Lập trình &amp; Cấu trúc Dữ liệu" có 4 bài dùng
/// chung <c>["Chiều sâu kỹ thuật","Giải quyết vấn đề &amp; thuật toán","Thuật ngữ chuyên ngành"]</c>;
/// trung bình 2,8 bài/chặng trên 87 chặng. Bằng chứng nhiễm chéo THẬT: bài "Phân tích và tối ưu
/// hiệu năng truy vấn SQL" nhận câu hỏi về xử lý lỗi API — chủ đề của bài KHÁC cùng chặng.</para>
///
/// <para>Hai lớp được khoá ở đây: <b>hợp đồng dây</b> (tên khoá camelCase ra JSON) và
/// <b>bất biến "không có bài học thì không đổi một byte"</b> cho mọi caller cũ.</para>
/// </summary>
public class LessonContextWireTests
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

    // ═══════════ 1. HỢP ĐỒNG DÂY — JSON gửi sang AIService ═══════════

    /// <summary>
    /// 🔒 KHOÁ TÊN KHOÁ RA DÂY: <c>lessonContext.title</c> / <c>lessonContext.outline</c>.
    /// Đổi tên KHÔNG ném lỗi ở đâu cả — pydantic <c>extra='ignore'</c> nuốt im lặng và câu hỏi lặng
    /// lẽ quay về bám CHẶNG. Repo đã dính đúng lớp bug này 4 lần (<c>focusCriteria</c>/BC14 ·
    /// <c>metricsVersion</c> · <c>adaptiveMaxQuestions</c> · <c>seniority</c>/SEN1).
    /// </summary>
    [Fact]
    public async Task GuiLessonContext_RaDayDungTenKhoaCamelCase()
    {
        var (gen, handler) = Generator();

        await gen.GenerateQuestionsAsync(
            "BE", cvText: null, jdText: null, focusCriteria: null, count: 5,
            grounding: null, "vi", criteria: null, "Junior",
            new LessonContext("Tổng quan OOP", "Đóng gói\nKế thừa"));

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var lc = doc.RootElement.GetProperty("lessonContext");
        Assert.Equal("Tổng quan OOP", lc.GetProperty("title").GetString());
        Assert.Equal("Đóng gói\nKế thừa", lc.GetProperty("outline").GetString());
    }

    /// Mục lục vắng là hợp lệ (người học bấm Bắt đầu mà chưa mở bài) — tiêu đề vẫn phải đi.
    [Fact]
    public async Task KhongCoMucLuc_VanGuiTieuDe()
    {
        var (gen, handler) = Generator();

        await gen.GenerateQuestionsAsync(
            "BE", null, null, null, 5, null, "vi", null, "Junior",
            new LessonContext("Tổng quan OOP", null));

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var lc = doc.RootElement.GetProperty("lessonContext");
        Assert.Equal("Tổng quan OOP", lc.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, lc.GetProperty("outline").ValueKind);
    }

    /// <summary>
    /// BẤT BIẾN LÙI: caller cũ (luyện tự do, campaign B2B) không đổi một byte — payload KHÔNG được
    /// mọc thêm khối nào có nội dung. Thiếu vế này thì "additive" chỉ là lời hứa.
    /// </summary>
    [Fact]
    public async Task KhongCoBaiHoc_PayloadKhongMangLessonContext()
    {
        var (gen, handler) = Generator();

        await gen.GenerateQuestionsAsync("BE", null, null, "Junior");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.True(
            !doc.RootElement.TryGetProperty("lessonContext", out var lc)
            || lc.ValueKind == JsonValueKind.Null);
    }

    /// Tiêu đề rỗng không phân biệt được bài nào với bài nào ⇒ bỏ cả khối, đừng gửi khối rỗng nghĩa.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TieuDeRong_BoCaKhoi(string title)
    {
        var (gen, handler) = Generator();

        await gen.GenerateQuestionsAsync(
            "BE", null, null, null, 5, null, "vi", null, "Junior", new LessonContext(title, "x"));

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.True(
            !doc.RootElement.TryGetProperty("lessonContext", out var lc)
            || lc.ValueKind == JsonValueKind.Null);
    }

    // ═══════════ 2. RÚT MỤC LỤC TỪ BÀI GIẢNG ═══════════

    [Fact]
    public void MucLuc_ChiLayDeMucCap2()
    {
        var md = """
                 # Tiêu đề bài

                 ## Định nghĩa

                 Nội dung dài dòng.

                 ### Chi tiết nhỏ

                 ## Ví dụ thực tế
                 """;

        var outline = LessonOutline.From(md);

        Assert.Equal("Định nghĩa\nVí dụ thực tế", outline);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# Chỉ có tiêu đề\n\nKhông đề mục nào.")]
    public void MucLuc_KhongRutDuoc_TraNull(string? md)
        => Assert.Null(LessonOutline.From(md));

    /// <summary>
    /// Trần là bắt buộc: bài giảng dài nhất trên dev là 47.655 ký tự, và một bài bất thường sẽ đẩy
    /// prompt sinh câu hỏi phình theo mà triệu chứng duy nhất là hoá đơn token cuối tháng.
    /// </summary>
    [Fact]
    public void MucLuc_CatTranSoDeMuc()
    {
        var md = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"## Đề mục {i}"));

        var lines = LessonOutline.From(md)!.Split('\n');

        Assert.Equal(LessonOutline.MaxHeadings, lines.Length);
        Assert.Equal("Đề mục 1", lines[0]);
    }

    [Fact]
    public void MucLuc_CatDeMucQuaDai_CoDauHieuDaCat()
    {
        var md = "## " + new string('x', LessonOutline.MaxHeadingLength + 50);

        var outline = LessonOutline.From(md)!;

        Assert.Equal(LessonOutline.MaxHeadingLength + 1, outline.Length);   // +1 = ký tự "…"
        Assert.EndsWith("…", outline);
    }

    /// <c>##Foo</c> (thiếu dấu cách) KHÔNG phải heading markdown — đừng nuốt nó vào mục lục.
    [Fact]
    public void MucLuc_BoQuaDongKhongPhaiHeading()
        => Assert.Null(LessonOutline.From("##KhongPhaiHeading\n#### Cap4\nvăn bản thường"));
}
