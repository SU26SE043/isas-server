using System.Net;
using System.Text;
using System.Text.Json;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP2-BE1 (nửa DÂY) — bộ tiêu chí chấm của chiến dịch phải ra được dây với ĐÚNG tên khoá
/// <c>criteriaContext</c>, khớp từng chữ với field pydantic cùng tên.
///
/// <para>🔴 <b>Vì sao đây là nửa quyết định của tính năng:</b> lệch tên khoá KHÔNG ném lỗi ở đâu cả.
/// <c>GenerateQuestionsRequest</c> bên Python không set <c>model_config</c> nên pydantic
/// <c>extra='ignore'</c> <b>nuốt im lặng</b> — .NET vẫn gửi, HTTP vẫn 200, prompt chỉ đơn giản không
/// đổi một chữ. Lớp bug này đã cắn repo <b>bốn lần</b> (<c>focusCriteria</c>/BC14 ·
/// <c>metricsVersion</c> · <c>adaptiveMaxQuestions</c> · <c>transcriptEngine</c>). Nửa còn lại của
/// hợp đồng nằm ở <c>tests/test_criteria_context_wire_cmp2.py::test_schema_khai_criteriaContext</c>.</para>
///
/// <para>⚠ <b>BẪY ĐÃ ĐO, đừng dựng lại:</b> <c>System.Text.Json</c> escape non-ASCII, nên
/// <c>"Chiều sâu"</c> nằm trong body thô dưới dạng <c>"Chiều sâu"</c>. Một assert
/// <c>Contains("Chiều sâu", handler.Body)</c> sẽ <b>XANH một cách tầm thường kể cả khi dữ liệu đã
/// rơi mất</b> — nó chỉ đang khẳng định chuỗi đó KHÔNG có mặt theo một cách khác. Ở đây mọi phép so
/// giá trị đi qua <c>JsonDocument</c> (nó giải mã lại), còn phép so trên body THÔ dùng
/// <b>sentinel ASCII</b>. Tiền lệ: F17.</para>
///
/// <para>⚠ Đã probe thật (SEN1): <c>JsonContent.Create</c> dùng <c>JsonSerializerDefaults.Web</c> nên
/// CÓ áp camelCase — viết <c>CriteriaContext</c> vẫn ra <c>"criteriaContext"</c>. Nên phép đối chứng
/// "không có khoá PascalCase" là vô nghĩa (luôn đúng); thứ đáng khoá là đúng TÊN, và mutation tương
/// ứng phải ĐỔI TÊN chứ không đổi hoa/thường.</para>
/// </summary>
public class CampaignCriteriaContextWireCmp2Tests
{
    // Sentinel ASCII — dùng khi phải soi BODY THÔ (xem docblock: tiếng Việt bị escape ở đó).
    private const string AsciiName = "ZZTOPCRITERION";
    private const string AsciiDesc = "ZZDESCRIPTIONSENTINEL";

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

    private static JsonElement Root(CapturingHandler h) => JsonDocument.Parse(h.Body!).RootElement;

    private static QuestionCriterionContext[] Ctx(params (string Name, string? Desc)[] items)
        => items.Select(i => new QuestionCriterionContext(i.Name, i.Desc)).ToArray();

    // ───────────────────── Tên khoá + shape ─────────────────────

    /// <summary>
    /// 🔒 Hợp đồng là TÊN khoá <c>criteriaContext</c> và tên hai field con <c>name</c>/<c>description</c>.
    /// Đổi bất kỳ cái nào ⇒ pydantic nuốt im lặng ⇒ tính năng chết câm.
    /// </summary>
    [Fact]
    public async Task Payload_DungTenKhoa_criteriaContext()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior",
            Ctx(("Chiều sâu kỹ thuật", "Hiểu sâu cơ chế")), default);

        var root = Root(handler);
        Assert.Contains("criteriaContext", root.EnumerateObject().Select(p => p.Name));

        var first = root.GetProperty("criteriaContext")[0];
        Assert.Contains("name", first.EnumerateObject().Select(p => p.Name));
        Assert.Contains("description", first.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>Giá trị ra dây đúng nội dung + đúng THỨ TỰ caller đưa vào (thứ tự HR sắp).</summary>
    [Fact]
    public async Task Payload_ChuyenDungTenVaMoTa_GiuThuTu()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior",
            Ctx(("Chiều sâu kỹ thuật", "Hiểu sâu cơ chế"),
                ("Thiết kế hệ thống", "Phân rã bài toán")), default);

        var arr = Root(handler).GetProperty("criteriaContext");
        Assert.Equal(2, arr.GetArrayLength());
        // Đi qua JsonDocument: nó giải mã \uXXXX nên so tiếng Việt ở đây là phép so THẬT.
        Assert.Equal("Chiều sâu kỹ thuật", arr[0].GetProperty("name").GetString());
        Assert.Equal("Hiểu sâu cơ chế", arr[0].GetProperty("description").GetString());
        Assert.Equal("Thiết kế hệ thống", arr[1].GetProperty("name").GetString());
    }

    /// <summary>
    /// Phép đo trên BODY THÔ — đây là chỗ duy nhất chứng minh chuỗi thật sự nằm trong request đã
    /// serialize. Dùng sentinel ASCII vì tiếng Việt bị escape ở tầng này (xem docblock).
    /// </summary>
    [Fact]
    public async Task BodyTho_ChuaThatSuTenVaMoTa_SentinelAscii()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior",
            Ctx((AsciiName, AsciiDesc)), default);

        Assert.Contains(AsciiName, handler.Body);
        Assert.Contains(AsciiDesc, handler.Body);
        Assert.Contains("criteriaContext", handler.Body);
    }

    /// <summary>
    /// 🔒 Đối chứng cho chính phép đo trên: chuỗi tiếng Việt KHÔNG xuất hiện nguyên văn trong body
    /// thô dù dữ liệu đi tới nơi đầy đủ.
    ///
    /// <para>Không phải trang trí — nó khoá lại lý do vì sao ba test kia phải dùng
    /// <c>JsonDocument</c>/sentinel. Ai đó "đơn giản hoá" thành
    /// <c>Assert.Contains("Chiều sâu kỹ thuật", handler.Body)</c> sẽ được một test XANH VĨNH VIỄN
    /// kể cả khi khoá bị đổi tên và dữ liệu rơi sạch.</para>
    /// </summary>
    [Fact]
    public async Task BodyTho_EscapeNonAscii_NenDungSentinel()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior",
            Ctx(("Chiều sâu kỹ thuật", null)), default);

        Assert.DoesNotContain("Chiều sâu kỹ thuật", handler.Body);
        // …nhưng dữ liệu VẪN tới nơi — đọc qua JsonDocument thì thấy.
        Assert.Equal("Chiều sâu kỹ thuật",
            Root(handler).GetProperty("criteriaContext")[0].GetProperty("name").GetString());
    }

    // ───────────────────── Không hồi quy: caller cũ ─────────────────────

    /// <summary>
    /// 🔒 KHÔNG HỒI QUY B2C/caller cũ: overload 5 tham số phải gửi <c>criteriaContext = null</c>,
    /// tức Python nhận <c>None</c> ⇒ prompt giữ NGUYÊN XI.
    ///
    /// <para>Khoá <c>ValueKind == Null</c> chứ không chỉ "không nổ": nếu ra dây là <c>[]</c> thì hôm
    /// nay vẫn vô hại (Python rẽ nhánh theo truthiness), nhưng nó nói sai ý — "chiến dịch khai một bộ
    /// tiêu chí rỗng" khác "chiến dịch chưa khai tiêu chí".</para>
    /// </summary>
    [Fact]
    public async Task Payload_OverloadCu_GuiNull_KhongPhaiMangRong()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Senior", default);

        Assert.Equal(JsonValueKind.Null, Root(handler).GetProperty("criteriaContext").ValueKind);
    }

    /// <summary>Overload 4 tham số (caller cũ nhất) — cũng phải null.</summary>
    [Fact]
    public async Task Payload_OverloadCuNhat_GuiNull()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null);

        Assert.Equal(JsonValueKind.Null, Root(handler).GetProperty("criteriaContext").ValueKind);
    }

    /// <summary>Danh sách rỗng ⇒ <c>null</c>, không phải <c>[]</c> (chiến dịch Draft chưa khai tiêu chí).</summary>
    [Fact]
    public async Task Payload_DanhSachRong_GuiNull()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior",
            Array.Empty<QuestionCriterionContext>(), default);

        Assert.Equal(JsonValueKind.Null, Root(handler).GetProperty("criteriaContext").ValueKind);
    }

    /// <summary>
    /// Tên rỗng/trắng bị LỌC. Toàn bộ danh sách chỉ có tên rỗng ⇒ <c>null</c>, KHÔNG phải <c>[]</c> —
    /// nếu gửi <c>[]</c> thì Python cũng bỏ qua, nhưng ta không được để hai đầu dựa vào việc đầu kia
    /// dọn hộ.
    /// </summary>
    [Fact]
    public async Task Payload_TenRong_BiLoc_ConLaiRong_ThiGuiNull()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior",
            Ctx(("", "mô tả"), ("   ", null)), default);

        Assert.Equal(JsonValueKind.Null, Root(handler).GetProperty("criteriaContext").ValueKind);
    }

    /// <summary>Lọc tên rỗng nhưng GIỮ dòng hợp lệ — không được vứt cả bộ vì một dòng hỏng.</summary>
    [Fact]
    public async Task Payload_LocTenRong_GiuDongHopLe()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior",
            Ctx(("  ", "bỏ"), (AsciiName, "giữ")), default);

        var arr = Root(handler).GetProperty("criteriaContext");
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal(AsciiName, arr[0].GetProperty("name").GetString());
    }

    /// <summary>Trim tên/mô tả — khoảng trắng thừa của HR không nên đi vào prompt.</summary>
    [Fact]
    public async Task Payload_TrimTenVaMoTa()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior",
            Ctx(("  Thuật toán  ", "  Phân tích độ phức tạp  ")), default);

        var first = Root(handler).GetProperty("criteriaContext")[0];
        Assert.Equal("Thuật toán", first.GetProperty("name").GetString());
        Assert.Equal("Phân tích độ phức tạp", first.GetProperty("description").GetString());
    }

    /// <summary>Mô tả null đi qua được — HR để trống mô tả là chuyện thường, không phải lỗi.</summary>
    [Fact]
    public async Task Payload_MoTaNull_KhongNo()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior",
            Ctx((AsciiName, null)), default);

        var first = Root(handler).GetProperty("criteriaContext")[0];
        Assert.Equal(AsciiName, first.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("description").ValueKind);
    }

    /// <summary>
    /// Field mới không được nuốt field cũ — chỗ dễ lệch nhất khi payload đổi shape (mẫu SEN1).
    /// </summary>
    [Fact]
    public async Task Payload_GiuNguyenCacFieldCu()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BA", "JD text", 7, "Middle", Ctx((AsciiName, null)), default);

        var root = Root(handler);
        Assert.Equal("BA", root.GetProperty("jobCategory").GetString());
        Assert.Equal("JD text", root.GetProperty("jdText").GetString());
        Assert.Equal(7, root.GetProperty("count").GetInt32());
        Assert.Equal("Middle", root.GetProperty("seniority").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cvText").ValueKind);
    }

    /// <summary>
    /// 🔒 KHÔNG gửi <c>weight</c>/<c>maxScore</c>/<c>criterionId</c> xuống prompt.
    ///
    /// <para>Không phải nit tiết kiệm byte: đưa trọng số vào prompt là <b>ngầm ra lệnh cho model
    /// phân bổ số câu theo trọng số</b> — tức đúng ràng buộc PHỦ ĐỀU mà CMP2 cố ý hoãn sang
    /// <c>SC2</c> (bộ tiêu chí campaign chưa có <c>scoring_scope</c> nên không phân biệt được tiêu
    /// chí CÁCH NÓI với tiêu chí NỘI DUNG; ép phủ đều sẽ đẻ ra câu hỏi phỏng vấn cho "Ngữ pháp &amp;
    /// dùng từ"). Record cố ý không mang các field đó, và test này khoá lại điều ấy ở tầng dây.</para>
    /// </summary>
    [Fact]
    public async Task Payload_KhongLoTrongSoVaThangDiem()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior", Ctx((AsciiName, AsciiDesc)), default);

        var names = Root(handler).GetProperty("criteriaContext")[0]
            .EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "name", "description" }, names);
        Assert.DoesNotContain("weight", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maxScore", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("criterionId", handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔒 KHÔNG dùng lại khoá <c>criteria</c> sẵn có: đó là đường GẮN NHÃN
    /// (<c>targetCriterionIds</c>) và nó kéo theo ràng buộc PHÂN BỔ BẮT BUỘC của SC1 — đúng thứ đợt
    /// này cố ý chưa làm. Bên Python hai khoá còn nằm ở hai nhánh loại trừ nhau (<c>if criteria</c>
    /// … <c>elif criteria_context</c>), nên gửi nhầm khoá sẽ làm khối bối cảnh <b>không bao giờ hiện</b>.
    /// </summary>
    [Fact]
    public async Task Payload_KhongGuiNhamVaoKhoaCriteria()
    {
        var (sut, handler) = Sut();

        await sut.GenerateAsync("BE", "JD", null, "Junior", Ctx((AsciiName, null)), default);

        Assert.DoesNotContain("criteria", Root(handler).EnumerateObject().Select(p => p.Name));
    }
}
