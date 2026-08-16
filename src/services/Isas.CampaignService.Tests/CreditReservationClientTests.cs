using System.Net;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-19 — Campaign giữ/trừ credit ví Org cho lượt chấm thử tính phí.
///
/// <para>PaymentService KHÔNG phải sửa gì: trường <c>sessionId</c> bên đó là khoá idempotency chứ
/// không phải tham chiếu buổi thi (BC7b đã dùng đúng như vậy cho cv-analysis). Ở đây khoá là
/// <c>rubric_preview_runs.id</c> — test dưới khoá đúng điều đó.</para>
/// </summary>
public class CreditReservationClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public string? CapturedPath { get; private set; }
        public string? CapturedBody { get; private set; }
        public string? CapturedToken { get; private set; }
        public int Calls { get; private set; }

        public StubHandler(HttpStatusCode status, string body = "{}")
        {
            _status = status; _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            CapturedPath = req.RequestUri?.AbsolutePath;
            CapturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            CapturedToken = req.Headers.TryGetValues("X-Internal-Token", out var v) ? v.FirstOrDefault() : null;
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static CreditReservationClient NewClient(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://payment.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return new CreditReservationClient(http, config, NullLogger<CreditReservationClient>.Instance);
    }

    [Fact]
    public async Task Reserve_gui_ownerType_Org_va_operationId_lam_khoa_idempotency()
    {
        var runId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var handler = new StubHandler(HttpStatusCode.OK,
            $"{{\"reservationId\":\"{Guid.NewGuid()}\",\"reservedCredits\":1}}");

        var res = await NewClient(handler).ReserveAsync("Org", orgId, runId, default);

        Assert.Equal(1, res.ReservedCredits);
        Assert.Equal("/internal/credits/reserve", handler.CapturedPath);
        Assert.Equal("tkn", handler.CapturedToken);   // GEN-1: máy-máy, không qua gateway

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("Org", doc.RootElement.GetProperty("ownerType").GetString());
        Assert.Equal(orgId, doc.RootElement.GetProperty("ownerId").GetGuid());
        // Khoá idempotency = id của lượt chấm thử, gửi dưới tên `sessionId` theo hợp đồng có sẵn.
        Assert.Equal(runId, doc.RootElement.GetProperty("sessionId").GetGuid());
    }

    // 402 phải tách khỏi lỗi hạ tầng: một bên là "org hết tiền" (HR nạp thêm là xong), một bên là
    // "hệ thống hỏng". Trộn hai thứ thì FE không thể hiện đúng hành động tiếp theo.
    [Fact]
    public async Task Reserve_402_nem_InsufficientOrgCreditException()
    {
        var handler = new StubHandler(HttpStatusCode.PaymentRequired);
        await Assert.ThrowsAsync<InsufficientOrgCreditException>(() =>
            NewClient(handler).ReserveAsync("Org", Guid.NewGuid(), Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Reserve_loi_ha_tang_nem_DownstreamServiceException()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewClient(handler).ReserveAsync("Org", Guid.NewGuid(), Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Reserve_JSON_hong_nem_DownstreamServiceException()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "khong-phai-json");
        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewClient(handler).ReserveAsync("Org", Guid.NewGuid(), Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData("consume", "/internal/credits/consume")]
    [InlineData("release", "/internal/credits/release")]
    public async Task Consume_va_Release_gui_dung_duong_dan_va_chi_kem_khoa(string op, string path)
    {
        var runId = Guid.NewGuid();
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = NewClient(handler);

        if (op == "consume") await client.ConsumeAsync(runId, default);
        else await client.ReleaseAsync(runId, default);

        Assert.Equal(path, handler.CapturedPath);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(runId, doc.RootElement.GetProperty("sessionId").GetGuid());
        // owner lấy từ reservation phía Payment ⇒ KHÔNG gửi lại (gửi lại là mở đường cho hai nguồn
        // sự thật về chủ ví).
        Assert.False(doc.RootElement.TryGetProperty("ownerId", out _));
    }
}
