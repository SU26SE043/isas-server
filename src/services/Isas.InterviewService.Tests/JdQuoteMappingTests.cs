using System.Net;
using System.Text;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// `jdQuote` phải đi hết đường AIService → DTO của InterviewService.
///
/// <para>Trường này trả lời câu "requirement này lấy từ đâu TRONG JD CỦA TÔI" — khác hẳn
/// <c>citations</c> (tài liệu chuẩn ngành truy hồi từ Qdrant, không phải trích từ JD). AIService đã
/// verify quote là substring thật của <c>jdText</c> trước khi trả, nên việc còn lại ở đây là không
/// đánh rơi nó lúc map.</para>
///
/// <para>Đo qua stub <c>HttpMessageHandler</c> vì lỗi đánh rơi field xảy ra ở tầng
/// deserialize/map, không phải ở logic service — cùng khuôn với
/// <c>AiServiceInternalTokenQ2Tests</c>.</para>
/// </summary>
public class JdQuoteMappingTests
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
    public async Task JdQuote_DuocMapSangDto()
    {
        var sut = Analyzer("""
            {"mustHave":[{"text":"Docker","citations":[],
                          "jdQuote":"Thành thạo Docker và Kubernetes"}],
             "niceToHave":[{"text":"Terraform","citations":[],"jdQuote":"Biết Terraform là lợi thế"}]}
            """);

        var (mustHave, niceToHave) = await sut.SuggestJdRequirementsAsync("BE", "JD text", []);

        Assert.Equal("Thành thạo Docker và Kubernetes", Assert.Single(mustHave).JdQuote);
        Assert.Equal("Biết Terraform là lợi thế", Assert.Single(niceToHave).JdQuote);
    }

    // AIService không verify được quote ⇒ trả null. Đó là trạng thái BÌNH THƯỜNG (FE chỉ ẩn nút
    // "Xem trong JD"), requirement vẫn phải giữ nguyên.
    [Fact]
    public async Task ThieuHoacNullJdQuote_KhongLamHongRequirement()
    {
        var sut = Analyzer("""
            {"mustHave":[{"text":"Docker","citations":[]},
                         {"text":"SQL","citations":[],"jdQuote":null}],
             "niceToHave":[]}
            """);

        var (mustHave, _) = await sut.SuggestJdRequirementsAsync("BE", "JD text", []);

        Assert.Equal(2, mustHave.Count);
        Assert.All(mustHave, x => Assert.Null(x.JdQuote));
        Assert.Equal(["Docker", "SQL"], mustHave.Select(x => x.Text));
    }

    // "Không có quote" chỉ được có MỘT biểu diễn trên wire để FE chỉ cần kiểm null.
    [Fact]
    public async Task JdQuoteToanKhoangTrang_ThanhNull()
    {
        var sut = Analyzer("""
            {"mustHave":[{"text":"Docker","citations":[],"jdQuote":"   "}],"niceToHave":[]}
            """);

        var (mustHave, _) = await sut.SuggestJdRequirementsAsync("BE", "JD text", []);

        Assert.Null(Assert.Single(mustHave).JdQuote);
    }
}
