using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

public sealed class EntitlementClientTests
{
    [Fact]
    public async Task PaymentTimeout_FallsBackToFreeWithoutPremiumFeatures()
    {
        using var client = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://payment.test") };
        var sut = new EntitlementClient(client, Mock.Of<IConfiguration>(), NullLogger<EntitlementClient>.Instance);

        var entitlement = await sut.ResolveUserAsync(Guid.NewGuid());

        Assert.Equal(EntitlementSnapshot.Free, entitlement);
        // ĐẢO TIỀN ĐỀ có chủ đích: adaptive KHÔNG còn là quyền lợi theo gói (mọi tier tiêu 1 credit/buổi)
        // ⇒ Payment sập không được biến buổi đã trừ credit thành buổi luồng tĩnh. Trần vẫn 0 = "không có
        // trần riêng" (PracticeService rơi về trần cấu hình), KHÔNG phải "0 câu".
        Assert.True(entitlement.AdaptiveEnabled);
        Assert.Equal(0, entitlement.MaxQuestions);
        Assert.False(entitlement.GroundingEnabled);
        Assert.False(entitlement.CvAnalysisIncluded);
        Assert.False(entitlement.RepoAnalysisIncluded);
        Assert.False(entitlement.RoadmapEnabled);
    }

    /// <summary>
    /// Gói bật adaptive nhưng KHÔNG khai trần (admin tạo plan để trống cap — hợp lệ theo
    /// <c>PlanService.Validate</c>) ⇒ trần phải map về <b>0 = "không có trần riêng"</b> để
    /// <c>PracticeService</c> rơi về trần cấu hình.
    ///
    /// Mặc định cũ <c>?? 10</c> / <c>?? 3</c> là hằng số ma nằm ở tầng client: nó bóp buổi còn một nửa
    /// so với cấu hình mà không ai khai con số đó ở đâu, và triệu chứng duy nhất là "sao buổi ngắn thế".
    /// Không có test này thì đổi 0 ↔ 10 chạy qua xanh.
    /// </summary>
    [Fact]
    public async Task GoiKhongKhaiTran_MapVe0_ChuKhongPhaiHangSoMa()
    {
        const string snapshot = """
            {"audience":0,"code":"custom","rank":1,"funding":0,"monthlyQuota":null,
             "adaptiveEnabled":true,"adaptiveMaxQuestions":null,"adaptiveMaxFollowups":null,
             "groundingEnabled":false,"selfConsistencyN":1,"cvAnalysisIncluded":false,
             "repoAnalysisIncluded":false,"roadmapEnabled":false,"maxActiveCampaigns":null,
             "maxCandidatesCap":null,"seatCount":null,"postpaidEligible":false,
             "entitlementsJson":"[]","entitlementsVersion":1,"json":"","hash":""}
            """;
        var body = $$"""
            {"source":"resolved","tierCode":"custom","tierRank":1,
             "entitlementSnapshot":{{System.Text.Json.JsonSerializer.Serialize(snapshot)}}}
            """;
        using var client = new HttpClient(new OkHandler(body)) { BaseAddress = new Uri("http://payment.test") };
        var sut = new EntitlementClient(client, Mock.Of<IConfiguration>(), NullLogger<EntitlementClient>.Instance);

        var e = await sut.ResolveUserAsync(Guid.NewGuid());

        Assert.True(e.AdaptiveEnabled);
        Assert.Equal(0, e.MaxQuestions);
        Assert.Equal(0, e.MaxFollowUps);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Payment unavailable");
    }

    private sealed class OkHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
