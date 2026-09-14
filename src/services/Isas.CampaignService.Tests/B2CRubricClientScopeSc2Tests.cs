using System.Net;
using System.Text;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SC2 · W5 (phía NHẬN) — <c>GET /internal/rubrics/b2c</c> nay mang <c>criteria[].scoringScope</c>.
/// Khoá JSON là <c>scoringScope</c> (camelCase) — lệch tên là field rụng im lặng về Always (lớp bug
/// đã cắn repo 4 lần). Vắng (Interview bản cũ) ⇒ Always; lạ ⇒ Always + không ném (chiều an toàn của
/// INT-18 là chấm THỪA, và chuỗi lạ từ Interview không phải lỗi của HR để trả 400/502).
/// </summary>
public class B2CRubricClientScopeSc2Tests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private static CampaignSessionClient NewClient(string json)
    {
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("http://interview.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return new CampaignSessionClient(http, config, NullLogger<CampaignSessionClient>.Instance);
    }

    private static string Body(string scopeJsonFragment) => $$"""
        {
          "jobCategory": "BE", "language": "vi", "version": 3,
          "criteria": [
            { "name": "Chiều sâu kỹ thuật", "weight": 0.6, "maxScore": 5, "levels": [] {{scopeJsonFragment}} },
            { "name": "Giao tiếp", "weight": 0.4, "maxScore": 5, "levels": [] }
          ]
        }
        """;

    [Fact]
    public async Task ScoringScope_DocDungKhoaCamelCase()
    {
        var res = await NewClient(Body(""", "scoringScope": "WhenTargeted" """)).GetB2CRubricAsync("BE", "vi");

        Assert.Equal("WhenTargeted", res.Criteria.Single(c => c.Name == "Chiều sâu kỹ thuật").ScoringScope);
        Assert.Equal("Always", res.Criteria.Single(c => c.Name == "Giao tiếp").ScoringScope);   // vắng ⇒ Always
    }

    // Khoá SAI tên (PascalCase / tên khác) ⇒ coi như vắng ⇒ Always. Test này tồn tại để chứng minh
    // phép đọc là theo đúng MỘT khoá, không phải "đọc được bất kỳ thứ gì có chữ scope".
    [Theory]
    [InlineData(""", "scoring_scope": "WhenTargeted" """)]
    [InlineData(""", "scope": "WhenTargeted" """)]
    public async Task ScoringScope_KhoaLechTen_RoiVeAlways(string fragment)
    {
        var res = await NewClient(Body(fragment)).GetB2CRubricAsync("BE", "vi");

        Assert.Equal("Always", res.Criteria.Single(c => c.Name == "Chiều sâu kỹ thuật").ScoringScope);
    }

    [Theory]
    [InlineData(""", "scoringScope": "Sometimes" """)]
    [InlineData(""", "scoringScope": "" """)]
    [InlineData(""", "scoringScope": null """)]
    public async Task ScoringScope_GiaTriLa_Always_KhongNem(string fragment)
    {
        var res = await NewClient(Body(fragment)).GetB2CRubricAsync("BE", "vi");

        Assert.Equal("Always", res.Criteria.Single(c => c.Name == "Chiều sâu kỹ thuật").ScoringScope);
    }

    [Fact]
    public async Task ScoringScope_KhongPhanBietHoaThuong_ChuanHoaVeTenChuan()
    {
        var res = await NewClient(Body(""", "scoringScope": "whentargeted" """)).GetB2CRubricAsync("BE", "vi");

        Assert.Equal("WhenTargeted", res.Criteria.Single(c => c.Name == "Chiều sâu kỹ thuật").ScoringScope);
    }
}
