using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// 2026-10-03 — chấm thử CHỈ chấm câu trả lời HR tự nhập. Khoá ở tầng DÂY: AIService mặc định
/// <c>includeAiSamples = true</c> (hợp đồng cũ), nên quên gửi cờ này thì nó âm thầm quay lại sinh 3 bài
/// Yếu/Khá/Xuất sắc (1 lượt sinh + 4 lượt chấm) mà không test nào ở tầng service thấy — mock của chúng
/// đứng thay chính client này.
/// </summary>
public class AiServiceRubricPreviewClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"samples":[{"band":"Custom","answerText":"bài","wordCount":1,"scores":[]}],"promptVersion":2,"lengthParityWarning":false}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task Payload_luon_gui_includeAiSamples_false_va_dung_cau_tra_loi_HR()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        var client = new AiServiceRubricPreviewClient(http, config, NullLogger<AiServiceRubricPreviewClient>.Instance);

        var result = await client.RunAsync("BE", "vi", "Junior", "Câu hỏi?", "đáp án mẫu", "bài của HR", 160,
            new List<PreviewCriterionInput>());

        var body = JsonNode.Parse(handler.CapturedBody!)!.AsObject();
        Assert.False(body["includeAiSamples"]!.GetValue<bool>());
        Assert.Equal("bài của HR", body["customAnswer"]!.GetValue<string>());
        Assert.Equal("Custom", Assert.Single(result.Samples).Band);
    }
}
