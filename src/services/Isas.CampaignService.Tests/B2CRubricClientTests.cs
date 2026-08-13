using System.Net;
using System.Text;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-20 — <c>CampaignSessionClient.GetB2CRubricAsync</c> đọc bộ chuẩn B2C từ Interview
/// (<c>GET /internal/rubrics/b2c</c>).
///
/// <para>Nhóm test đắt nhất ở đây là các vế THẤT BẠI: mọi đường hỏng phải ném
/// <see cref="DownstreamServiceException"/> chứ KHÔNG được trả về một bộ tiêu chí bịa. Chép một bộ
/// bịa vào campaign không có triệu chứng nào — Interview vẫn chấm ra điểm, HR vẫn thấy bảng xếp
/// hạng, chỉ có thước đo là thứ chưa ai viết.</para>
/// </summary>
public class B2CRubricClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;
        public string? CapturedUri { get; private set; }
        public string? CapturedToken { get; private set; }

        public StubHandler(HttpStatusCode status, string json)
        {
            _status = status;
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri?.PathAndQuery;
            CapturedToken = request.Headers.TryGetValues("X-Internal-Token", out var v)
                ? string.Join(",", v) : null;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static CampaignSessionClient NewClient(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://interview.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return new CampaignSessionClient(http, config, NullLogger<CampaignSessionClient>.Instance);
    }

    private const string BoDayDu = """
        {
          "jobCategory": "BE",
          "language": "vi",
          "version": 3,
          "criteria": [
            { "name": "Chuyên môn", "description": "Kiến thức nền", "weight": 0.6, "maxScore": 5,
              "levels": [ { "score": 5, "descriptor": "Xuất sắc" }, { "score": 0, "descriptor": "Trống" } ] },
            { "name": "Giao tiếp", "description": null, "weight": 0.4, "maxScore": 5, "levels": [] }
          ]
        }
        """;

    // Đường thành công: map đủ 5 field mỗi tiêu chí + mốc, và ECHO đúng tổ hợp đã hỏi.
    [Fact]
    public async Task DocDuocBoChuan_MapDuFieldVaMoc()
    {
        var handler = new StubHandler(HttpStatusCode.OK, BoDayDu);
        var client = NewClient(handler);

        var result = await client.GetB2CRubricAsync("BE", "vi");

        Assert.Equal("BE", result.JobCategory);
        Assert.Equal("vi", result.Language);
        Assert.Equal(3, result.Version);
        Assert.Equal(2, result.Criteria.Count);

        var chuyenMon = result.Criteria[0];
        Assert.Equal("Chuyên môn", chuyenMon.Name);
        Assert.Equal("Kiến thức nền", chuyenMon.Description);
        Assert.Equal(0.6m, chuyenMon.Weight);
        Assert.Equal(5, chuyenMon.MaxScore);
        // Mốc sort tăng dần theo score — `.Include()` phía Interview KHÔNG bảo đảm thứ tự, và JSON mẫu
        // ở đây cố ý trả 5 trước 0 để phép sort là thứ THẬT SỰ được kiểm.
        Assert.Equal(new[] { 0, 5 }, chuyenMon.Levels.Select(l => l.Score));
        Assert.Equal("Trống", chuyenMon.Levels[0].Descriptor);

        // levels rỗng = CHƯA KHAI MỐC, hợp lệ (Interview rơi về dải mặc định) — không được ném.
        Assert.Empty(result.Criteria[1].Levels);
        Assert.Null(result.Criteria[1].Description);
    }

    // Query string + gate GEN-7. Sai đường dẫn/thiếu token thì Interview trả 404/401, mà cả hai đều
    // hiện ra dưới dạng "chưa có bộ chuẩn" — tức lỗi cấu hình đội lốt lỗi nghiệp vụ.
    [Fact]
    public async Task GoiDungDuongDan_VaGanInternalToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK, BoDayDu);
        var client = NewClient(handler);

        await client.GetB2CRubricAsync("BE", "en");

        Assert.Equal("/internal/rubrics/b2c?jobCategory=BE&language=en", handler.CapturedUri);
        Assert.Equal("tkn", handler.CapturedToken);
    }

    // 404 = admin chưa soạn bộ cho tổ hợp này. Vẫn ném (KHÔNG fallback), nhưng thông điệp phải nói
    // đúng việc cần làm chứ không phải "lỗi hệ thống".
    //
    // ⚠ TIỀN ĐỀ ĐÃ ĐỔI CÓ CHỦ ĐÍCH: bản trước khẳng định loại ném ra là ĐÚNG
    // `DownstreamServiceException`. Nay là loại DẪN XUẤT `SystemRubricNotFoundException` để đường XEM
    // TRƯỚC phân biệt được "chưa ai soạn" (→404) với "hệ thống hỏng" (→502). Không nới assert thành
    // `ThrowsAnyAsync`: loại chính xác là thứ quyết định mã HTTP employer nhận được.
    [Fact]
    public async Task ChuaCoBoChuan_404_NemLoaiRieng_KemThongDiepNoiDungViec()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "not found");
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<SystemRubricNotFoundException>(
            () => client.GetB2CRubricAsync("BA", "en"));

        Assert.Contains("BA", ex.Message);
        Assert.Contains("en", ex.Message);

        // 🔴 Vế thứ hai KHÔNG được bỏ: nhờ KẾ THỪA mà khối `catch (DownstreamServiceException)` của
        // đường CHÉP vẫn bắt được loại này ⇒ hợp đồng 502 đã chốt với FE không đổi. Tách thành hai lớp
        // rời nhau sẽ làm đường chép rơi xuống `catch (Exception)` → 500 với MỌI ca "chưa có bộ chuẩn".
        Assert.IsAssignableFrom<DownstreamServiceException>(ex);
    }

    // Token sai (401) / Interview lỗi (500) → ném, KHÔNG trả bộ bịa.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonSuccess_Nem(HttpStatusCode status)
    {
        var handler = new StubHandler(status, "boom");
        var client = NewClient(handler);

        await Assert.ThrowsAsync<DownstreamServiceException>(() => client.GetB2CRubricAsync("BE", "vi"));
    }

    // 200 nhưng bộ RỖNG → ném. Chép bộ rỗng về sẽ xoá sạch tiêu chí của chiến dịch rồi để lại một
    // campaign không thước đo, mà Interview vẫn chấm ra điểm ⇒ hỏng hoàn toàn im lặng.
    [Theory]
    [InlineData("""{ "jobCategory": "BE", "language": "vi", "version": 1, "criteria": [] }""")]
    [InlineData("""{ "jobCategory": "BE", "language": "vi", "version": 1 }""")]
    public async Task BoRong_Nem(string json)
    {
        var handler = new StubHandler(HttpStatusCode.OK, json);
        var client = NewClient(handler);

        await Assert.ThrowsAsync<DownstreamServiceException>(() => client.GetB2CRubricAsync("BE", "vi"));
    }

    // Field LẠ bị bỏ qua — Interview thêm field mới (vd scoringScope) không được làm vỡ bên này.
    [Fact]
    public async Task FieldLa_BoQua_KhongVo()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {
              "jobCategory": "FE", "language": "vi", "version": 2, "somethingNew": 42,
              "criteria": [ { "id": "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "name": "A",
                              "weight": 1.0, "maxScore": 5, "scoringScope": "Always", "levels": [] } ]
            }
            """);
        var client = NewClient(handler);

        var result = await client.GetB2CRubricAsync("FE", "vi");

        Assert.Single(result.Criteria);
        Assert.Equal("A", result.Criteria[0].Name);
    }
}
