using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// BE-1 — <b>HỢP ĐỒNG DÂY cho <c>criteria</c></b> giữa InterviewService và <c>/generate-roadmap</c>.
///
/// <para>BE-1 sinh ra để diệt bug: <c>build_roadmap_prompt</c> không nhận danh sách tiêu chí nên
/// model tự bịa tên <c>focusCriteria</c> — đo trên production chỉ <b>25/359 tên khớp rubric = 7%</b>.
/// Hỏng dây chuyền: <c>BuildWeaknesses</c> giao baseline (tên thật) với <c>focusCriteria</c> (tên
/// bịa) ra RỖNG ⇒ bài giảng không bao giờ nhận được điểm yếu, và hỏng IM LẶNG.</para>
///
/// <para><b>Vì sao cần đúng bài kiểm này:</b> kiểm định độc lập chạy phép đột biến "đổi tên trường
/// JSON <c>criteria</c> → <c>criteriaList</c>" và <b>không bài kiểm nào đỏ</b>. Phía Python đã khoá
/// tên khoá; phía .NET thì không. Mà lệch tên KHÔNG ném lỗi ở đâu cả — pydantic <c>extra='ignore'</c>
/// nuốt im lặng, đúng lớp lỗi repo đã dính <b>ba lần</b> (<c>focusCriteria</c>/BC14 ·
/// <c>metricsVersion</c> · <c>adaptiveMaxQuestions</c>). Mẫu đúng đã có sẵn ở
/// <see cref="SeniorityWireSen1Tests"/>; bài này áp cùng mẫu cho <c>criteria</c>.</para>
///
/// <para>⚠ Một phép đối chứng kiểu "không có khoá PascalCase" là VÔ NGHĨA ở đây:
/// <c>JsonContent.Create</c> dùng <c>JsonSerializerDefaults.Web</c> nên luôn ra camelCase. Phép có
/// nghĩa là khoá đúng TÊN và đúng HÌNH DẠNG phần tử, nên phép đột biến tương ứng phải đổi tên chứ
/// không đổi hoa/thường.</para>
/// </summary>
public class RoadmapCriteriaWireBe1Tests
{
    private sealed class CaptureHandler(string json) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (AiServiceRoadmapGenerator gen, CaptureHandler handler) Generator()
    {
        var handler = new CaptureHandler("""{"milestones":[{"title":"M1","focusCriteria":[],"lessons":[{"title":"L1"}]}]}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000") };
        return (new AiServiceRoadmapGenerator(
            http, new ConfigurationBuilder().Build(),
            NullLogger<AiServiceRoadmapGenerator>.Instance), handler);
    }

    private static async Task<JsonElement> BodyAfterGenerateAsync(
        CaptureHandler handler, AiServiceRoadmapGenerator gen,
        IReadOnlyList<QuestionTargetCriterionDto>? criteria)
    {
        await gen.GenerateAsync("BA", "Junior", null, null, null, null, criteria, default);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// 🔒 Khoá TÊN KHOÁ ra dây là <c>criteria</c> — đây là điều phép đột biến "đổi tên trường" đã
    /// đi lọt qua toàn bộ bộ kiểm trước đó.
    /// </summary>
    [Fact]
    public async Task Wire_GuiDungTenKhoaCriteria()
    {
        var (gen, handler) = Generator();

        var body = await BodyAfterGenerateAsync(handler, gen, new[]
        {
            new QuestionTargetCriterionDto(Guid.NewGuid(), "Phân tích yêu cầu"),
        });

        Assert.Contains("criteria", body.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>
    /// 🔒 Khoá HÌNH DẠNG phần tử: pydantic <c>CriterionRef</c> đọc <c>criterionId</c> + <c>name</c>.
    /// Đổi tên field con cũng bị <c>extra='ignore'</c> nuốt im lặng y hệt đổi tên field cha, và khi
    /// đó danh sách tiêu chí tới nơi ở dạng rỗng nghĩa — model quay lại bịa tên, đúng bug ban đầu.
    /// </summary>
    [Fact]
    public async Task Wire_MoiPhanTuCoDungCriterionIdVaName()
    {
        var (gen, handler) = Generator();
        var id = Guid.NewGuid();

        var body = await BodyAfterGenerateAsync(handler, gen, new[]
        {
            new QuestionTargetCriterionDto(id, "Phân tích yêu cầu"),
        });

        var first = body.GetProperty("criteria").EnumerateArray().Single();
        Assert.Equal(id.ToString(), first.GetProperty("criterionId").GetString());
        Assert.Equal("Phân tích yêu cầu", first.GetProperty("name").GetString());
    }

    /// <summary>
    /// 🔒 Gửi ĐÚNG tên tiêu chí nhận được, KHÔNG cắt bớt và KHÔNG đổi chữ.
    ///
    /// <para>Chỗ này là nguồn của cả tính năng: phía Python so khớp tên rồi trả về TÊN CHUẨN, nên
    /// nếu .NET gửi lên tên đã bị biến dạng thì phép so khớp bên kia mất nghĩa. Bài kiểm khẳng định
    /// nguyên vẹn cả danh sách chứ không chỉ phần tử đầu — cắt danh sách còn 1 phần tử là một cách
    /// hỏng thật (model chỉ được chọn từ tập hẹp hơn thực tế rồi phần còn lại bị coi là "bịa").</para>
    /// </summary>
    [Fact]
    public async Task Wire_GuiNguyenVenDanhSachTenTieuChi()
    {
        var (gen, handler) = Generator();
        string[] names = ["Phân tích yêu cầu", "Hiểu nghiệp vụ & các bên liên quan", "Tư duy giải quyết vấn đề"];

        var body = await BodyAfterGenerateAsync(handler, gen,
            names.Select(n => new QuestionTargetCriterionDto(Guid.NewGuid(), n)).ToList());

        var sent = body.GetProperty("criteria").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Equal(names, sent);
    }

    /// <summary>
    /// 🔒 Không có tiêu chí ⇒ KHÔNG gửi danh sách rỗng, mà gửi <c>null</c>.
    ///
    /// <para>Phân biệt này là load-bearing phía Python: <c>known_names</c> rỗng nghĩa là "caller
    /// không có gì để đối chiếu" nên bộ lọc <b>không lọc gì</b> và giữ nguyên hành vi cũ. Nếu .NET
    /// gửi <c>[]</c> thay vì <c>null</c> thì ranh giới đó vẫn giữ, nhưng ý định đọc ra khác hẳn —
    /// khoá lại để một lần "dọn cho gọn" không âm thầm đổi ngữ nghĩa.</para>
    /// </summary>
    [Fact]
    public async Task Wire_KhongCoTieuChi_GuiNull_KhongPhaiMangRong()
    {
        var (gen, handler) = Generator();

        var body = await BodyAfterGenerateAsync(handler, gen, null);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("criteria").ValueKind);
    }
}
