using System.Net;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SEN1 (nửa B2B) — <c>campaigns.seniority</c> phải ra được dây khi HR bấm "sinh câu hỏi".
///
/// <para>Payload dựng bằng anonymous object và <c>JsonContent.Create</c> KHÔNG áp naming policy nào,
/// nên tên khoá thật sự ra dây chỉ được quyết định ở đúng một chỗ. Lệch tên với pydantic KHÔNG ném
/// lỗi ở đâu cả — field im lặng biến mất (repo đã dính 3 lần: <c>focusCriteria</c>/BC14 ·
/// <c>metricsVersion</c> · <c>adaptiveMaxQuestions</c>).</para>
///
/// <para>⚠ <b>Phạm vi thật của SEN1 phía B2B:</b> client này ĐÃ gửi được <c>seniority</c>, nhưng
/// caller duy nhất (<c>CampaignService.GenerateQuestionsAsync</c>) vẫn gọi overload cũ nên mọi chiến
/// dịch còn nhận <c>"Junior"</c>. File caller đó nằm ngoài phạm vi sở hữu của thay đổi này — xem
/// <see cref="IQuestionGenerator"/> để biết dòng cần sửa.</para>
/// </summary>
public class CampaignSeniorityWireSen1Tests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"questions":["Câu 1"]}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private static (AiServiceQuestionGenerator sut, CapturingHandler handler) Sut()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return (new AiServiceQuestionGenerator(
            http, config, NullLogger<AiServiceQuestionGenerator>.Instance), handler);
    }

    private static string SeniorityOf(CapturingHandler h)
    {
        using var doc = JsonDocument.Parse(h.Body!);
        return doc.RootElement.GetProperty("seniority").GetString()!;
    }

    [Theory]
    [InlineData("Fresher")]
    [InlineData("Junior")]
    [InlineData("Middle")]
    [InlineData("Senior")]
    public async Task Payload_ChuyenSeniorityCuaChienDich(string level)
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, level, default);

        Assert.Equal(level, SeniorityOf(handler));
    }

    /// <summary>
    /// 🔒 Hợp đồng là TÊN khoá <c>seniority</c>.
    ///
    /// <para>⚠ Đã probe thật: <c>JsonContent.Create</c> dùng <c>JsonSerializerDefaults.Web</c> nên CÓ
    /// áp camelCase — viết <c>Seniority</c> vẫn ra <c>"seniority"</c>. Nên phép đối chứng "không có
    /// khoá PascalCase" là vô nghĩa (luôn đúng); thứ đáng khoá là đúng TÊN, và mutation tương ứng
    /// phải ĐỔI TÊN chứ không đổi hoa/thường.</para>
    /// </summary>
    [Fact]
    public async Task Payload_DungTenKhoaSeniority()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Senior", default);

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Contains("seniority", doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>Overload cũ (caller B2B hiện tại) — vẫn phải ra dây một giá trị hợp lệ, không null.</summary>
    [Fact]
    public async Task Payload_OverloadCu_GuiJunior()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null);

        Assert.Equal("Junior", SeniorityOf(handler));
    }

    /// <summary>
    /// 🔒 KHÔNG BAO GIỜ để null ra dây: <c>GenerateQuestionsRequest.seniority</c> bên Python khai
    /// <c>str</c> (không Optional) ⇒ <c>null</c> là <b>422</b> ⇒ HR bấm "sinh câu hỏi" nhận 502 mà
    /// nguyên nhân thật nằm ở một field phụ.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Payload_SeniorityRong_HaVeJunior_KhongBaoGioNull(string? bad)
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, bad!, default);

        using var doc = JsonDocument.Parse(handler.Body!);
        var v = doc.RootElement.GetProperty("seniority");
        Assert.NotEqual(JsonValueKind.Null, v.ValueKind);
        Assert.Equal("Junior", v.GetString());
    }

    /// <summary>Không nắn giá trị lạ ở đây — AIService hạ về Junior VÀ ghi log (chỗ phát hiện caller sai).</summary>
    [Fact]
    public async Task Payload_GiaTriLa_GuiNguyenVan()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "CEO", default);

        Assert.Equal("CEO", SeniorityOf(handler));
    }

    /// <summary>Tham số mới không được nuốt các field cũ (chỗ dễ lệch nhất khi payload đổi shape).</summary>
    [Fact]
    public async Task Payload_GiuNguyenCacFieldCu()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BA", "JD text", 7, "Middle", default);

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("BA", doc.RootElement.GetProperty("jobCategory").GetString());
        Assert.Equal("JD text", doc.RootElement.GetProperty("jdText").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("cvText").ValueKind);
    }
}
