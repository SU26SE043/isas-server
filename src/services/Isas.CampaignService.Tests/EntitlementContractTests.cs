using System.Net;
using System.Text;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// B2 — vế CONSUMER (B2B) của hợp đồng entitlement snapshot; vế producer khoá ở
/// Isas.PaymentService.Tests/TierContractGuardTests.
/// Lệch tên field KHÔNG ném lỗi ⇒ caps rơi về default rồi bị coi là "invalid" ⇒ tụt về Starter
/// trong im lặng (chỉ có một dòng LogWarning). Test này khoá cả tên field lẫn ngữ nghĩa null.
/// </summary>
public sealed class EntitlementContractTests
{
    private static EntitlementClient Sut(string body)
    {
        var client = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://payment.test") };
        return new EntitlementClient(client, Mock.Of<IConfiguration>(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<EntitlementClient>.Instance, Options.Create(new TieringSettings { Enabled = true }));
    }

    private static string Response(string tier, string snapshotJson) => $$"""
        {"source":"resolved","tierCode":"{{tier}}","tierRank":1,
         "entitlementSnapshot":{{System.Text.Json.JsonSerializer.Serialize(snapshotJson)}}}
        """;

    [Fact]
    public async Task GoiBusiness_DocDungCaps_KhongTutVeStarter()
    {
        var snapshot = """
            {"audience":1,"code":"business","rank":1,"funding":0,"monthlyQuota":null,
             "adaptiveEnabled":true,"adaptiveMaxQuestions":null,"adaptiveMaxFollowups":null,
             "groundingEnabled":true,"selfConsistencyN":1,"cvAnalysisIncluded":false,
             "repoAnalysisIncluded":false,"roadmapEnabled":false,"maxActiveCampaigns":10,
             "maxCandidatesCap":200,"seatCount":10,"postpaidEligible":true,
             "entitlementsJson":"[]","entitlementsVersion":1,"json":"","hash":""}
            """;

        var e = await Sut(Response("business", snapshot)).ResolveOrgAsync(Guid.NewGuid());

        Assert.Equal("business", e.TierCode);
        Assert.Equal(10, e.MaxActiveCampaigns);
        Assert.Equal(200, e.MaxCandidatesCap);
        Assert.True(e.AdaptiveEnabled);
        Assert.True(e.GroundingEnabled);
        Assert.True(e.PostpaidEligible);
    }

    // H5 — Enterprise cố ý để caps = null nghĩa là KHÔNG GIỚI HẠN. Từng bị coi là "invalid" nên gói
    // đắt nhất tụt xuống đúng caps của gói free (1 campaign / 25 ứng viên) mà chỉ log một dòng.
    [Fact]
    public async Task GoiEnterprise_CapsNull_LaKhongGioiHan_KhongPhaiInvalid()
    {
        var snapshot = """
            {"audience":1,"code":"enterprise","rank":2,"funding":0,"monthlyQuota":null,
             "adaptiveEnabled":true,"adaptiveMaxQuestions":null,"adaptiveMaxFollowups":null,
             "groundingEnabled":true,"selfConsistencyN":1,"cvAnalysisIncluded":false,
             "repoAnalysisIncluded":false,"roadmapEnabled":false,"maxActiveCampaigns":null,
             "maxCandidatesCap":null,"seatCount":null,"postpaidEligible":true,
             "entitlementsJson":"[]","entitlementsVersion":1,"json":"","hash":""}
            """;

        var e = await Sut(Response("enterprise", snapshot)).ResolveOrgAsync(Guid.NewGuid());

        Assert.Equal("enterprise", e.TierCode);          // KHÔNG rơi về "starter"
        Assert.Equal(int.MaxValue, e.MaxActiveCampaigns);
        Assert.Equal(int.MaxValue, e.MaxCandidatesCap);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
