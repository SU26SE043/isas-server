using System.Net;
using System.Text;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// B2 — vế CONSUMER của hợp đồng entitlement snapshot (vế producer khoá ở
/// Isas.PaymentService.Tests/TierContractGuardTests).
///
/// Vì sao phải có: consumer deserialize bằng record riêng, nên lệch tên field KHÔNG ném lỗi —
/// System.Text.Json chỉ điền default (0/false) ⇒ gói trả phí âm thầm mất quyền. Đúng bug đã xảy
/// ra: producer emit `adaptiveMaxQuestions` còn consumer đọc `maxQuestions` ⇒ MỌI user Plus/Pro
/// nhận trần 0 câu. Cùng lớp với bug `focusCriteria` (BC14).
/// </summary>
public sealed class EntitlementContractTests
{
    // JSON Y HỆT thứ Payment sinh ra (EntitlementSnapshotBuilder, JsonSerializerDefaults.Web → camelCase)
    // cho gói "pro". Đổi tên khoá ở Payment mà quên sửa consumer ⇒ test này ĐỎ.
    private const string ProSnapshotJson = """
        {"audience":0,"code":"pro","rank":2,"funding":1,"monthlyQuota":100,
         "adaptiveEnabled":true,"adaptiveMaxQuestions":20,"adaptiveMaxFollowups":5,
         "groundingEnabled":true,"selfConsistencyN":3,"cvAnalysisIncluded":true,
         "repoAnalysisIncluded":true,"roadmapEnabled":true,"maxActiveCampaigns":null,
         "maxCandidatesCap":null,"seatCount":null,"postpaidEligible":false,
         "entitlementsJson":"[]","entitlementsVersion":1,"json":"","hash":""}
        """;

    [Fact]
    public async Task SnapshotGoiPro_DocRaDungTran_KhongPhaiZero()
    {
        var body = $$"""
            {"source":"resolved","tierCode":"pro","tierRank":2,
             "entitlementSnapshot":{{System.Text.Json.JsonSerializer.Serialize(ProSnapshotJson)}}}
            """;
        using var client = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://payment.test") };
        var sut = new EntitlementClient(client, Mock.Of<IConfiguration>(), NullLogger<EntitlementClient>.Instance);

        var e = await sut.ResolveUserAsync(Guid.NewGuid());

        Assert.Equal("pro", e.TierCode);
        Assert.True(e.AdaptiveEnabled);
        Assert.Equal(20, e.MaxQuestions);      // từng là 0 vì lệch tên field
        Assert.Equal(5, e.MaxFollowUps);       // từng là 0
        Assert.True(e.GroundingEnabled);
        Assert.Equal(3, e.SelfConsistencyN);
        Assert.True(e.CvAnalysisIncluded);
        Assert.True(e.RepoAnalysisIncluded);
        Assert.True(e.RoadmapEnabled);
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
