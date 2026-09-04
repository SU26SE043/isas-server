using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-16/18 — bộ thước đo gửi sang Interview lúc tạo buổi thi.
///
/// <para>Test quan trọng nhất ở đây là "payload = đúng đầu ra của <see cref="ScoringCriteriaBuilder"/>".
/// Cả tính năng chấm thử đứng trên lời hứa "thứ HR kiểm chứng chính là thứ ứng viên bị chấm"; nếu
/// đường chấm-thật dựng payload theo cách riêng thì hai đường trôi xa nhau mà KHÔNG CÓ TRIỆU CHỨNG —
/// cả hai vẫn ra điểm, chỉ là điểm của hai thước đo khác nhau.</para>
/// </summary>
public class ScoringCriteriaBuilderTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"id\":\"{Guid.NewGuid()}\",\"questions\":[]}}", Encoding.UTF8, "application/json")
            };
        }
    }

    private static CampaignSessionClient NewClient(CapturingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://interview.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return new CampaignSessionClient(http, config, NullLogger<CampaignSessionClient>.Instance);
    }

    private static CampaignCriterion Criterion(
        int orderNo, string name, decimal weight, int maxScore, params (int Score, string Text)[] levels)
    {
        var c = new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = Guid.NewGuid(), OrderNo = orderNo, Name = name,
            Description = "mô tả " + name, Weight = weight, MaxScore = maxScore,
            Source = CriterionSource.HrEdited, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        c.Levels = levels.Select(l => new CampaignCriterionLevel
        {
            Id = Guid.NewGuid(), CriterionId = c.Id, Score = l.Score, Descriptor = l.Text,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        }).ToList();
        return c;
    }

    private static List<CampaignCriterion> SampleRubric() => new()
    {
        Criterion(0, "Chuyên môn", 0.6m, 5, (5, "CÓ: nêu đủ ý và ví dụ"), (0, "CÓ: không nêu được ý nào")),
        Criterion(1, "Giao tiếp", 0.4m, 10, (0, "CÓ: nói rời rạc"), (10, "CÓ: mạch lạc, có cấu trúc")),
    };

    // ── Builder ───────────────────────────────────────────────────────────

    [Fact]
    public void Build_sap_tieu_chi_theo_OrderNo_va_moc_theo_Score()
    {
        var built = ScoringCriteriaBuilder.Build(SampleRubric().AsEnumerable().Reverse());

        Assert.Equal(new[] { "Chuyên môn", "Giao tiếp" }, built.Select(c => c.Name));
        Assert.Equal(new[] { 0, 5 }, built[0].Levels.Select(l => l.Score));
        Assert.Equal(new[] { 0, 10 }, built[1].Levels.Select(l => l.Score));
    }

    // Chưa khai mốc là trạng thái HỢP LỆ — Interview rơi về dải mặc định như trước CAMP-16.
    [Fact]
    public void Build_tieu_chi_chua_co_moc_tra_mang_rong_khong_nem()
    {
        var built = ScoringCriteriaBuilder.Build(new[] { Criterion(0, "Trống", 1.0m, 5) });
        Assert.Empty(Assert.Single(built).Levels);
    }

    // ── Payload gửi Interview ────────────────────────────────────────────

    [Fact]
    public async Task Payload_criteria_LA_DUNG_dau_ra_cua_builder()
    {
        var rubric = SampleRubric();
        var handler = new CapturingHandler();

        await NewClient(handler).CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            new[] { "Q1" }, ScoringCriteriaBuilder.Build(rubric), null, default);

        var sent = JsonNode.Parse(handler.CapturedBody!)!["criteria"]!.ToJsonString();

        // Cùng options mà JsonContent.Create dùng (JsonSerializerDefaults.Web ⇒ camelCase).
        var expected = JsonSerializer.Serialize(
            ScoringCriteriaBuilder.Build(rubric).Select(c => new
            {
                c.Name, c.Description, c.Weight, c.MaxScore,
                // RNK1 · HĐ-5 — khoá JSON "criterionId" (= campaign_criteria.id) đi giữa maxScore và levels.
                criterionId = c.CriterionId,
                levels = c.Levels.Select(l => new { l.Score, l.Descriptor })
            }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(JsonNode.Parse(expected)!.ToJsonString(), sent);
    }

    // RNK1 · HĐ-5 — test HỢP ĐỒNG dây `criterionId`: mỗi tiêu chí trên payload mang khoá JSON
    // camelCase "criterionId" = campaign_criteria.id. Interview ghi nó vào
    // rubric_criteria.source_criterion_id ⇒ snapshot chấm khớp điểm sàn read-time THEO ID (ổn định
    // qua PUT). Đổi tên khoá / bỏ field = Campaign khớp sàn theo TÊN, gãy ngay khi HR đổi tên tiêu chí.
    [Fact]
    public async Task Payload_moi_tieu_chi_mang_khoa_criterionId_bang_campaign_criteria_id()
    {
        var rubric = SampleRubric();
        var handler = new CapturingHandler();

        await NewClient(handler).CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            new[] { "Q1" }, ScoringCriteriaBuilder.Build(rubric), null, default);

        var criteria = JsonNode.Parse(handler.CapturedBody!)!["criteria"]!.AsArray();
        Assert.Equal(rubric.Count, criteria.Count);
        for (var i = 0; i < rubric.Count; i++)
        {
            var node = criteria[i]!.AsObject();
            Assert.True(node.ContainsKey("criterionId"), "thiếu khoá 'criterionId' trên payload tiêu chí");
            Assert.Equal(rubric[i].Id, (Guid)node["criterionId"]!);
        }
    }

    [Fact]
    public async Task Payload_mang_levels_dung_noi_dung_moc()
    {
        var handler = new CapturingHandler();
        await NewClient(handler).CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            new[] { "Q1" }, ScoringCriteriaBuilder.Build(SampleRubric()), null, default);

        var levels = JsonNode.Parse(handler.CapturedBody!)!["criteria"]![0]!["levels"]!.AsArray();
        Assert.Equal(2, levels.Count);
        Assert.Equal(0, (int)levels[0]!["score"]!);
        Assert.Equal("CÓ: không nêu được ý nào", (string)levels[0]!["descriptor"]!);
        Assert.Equal(5, (int)levels[1]!["score"]!);
    }

    // Bộ chưa khai mốc PHẢI gửi `levels: []` chứ không phải bỏ field — Interview cũ bỏ qua field lạ,
    // Interview mới đọc mảng rỗng thành "dùng dải mặc định". Hai service deploy không nguyên tử.
    [Fact]
    public async Task Payload_van_co_levels_rong_khi_chua_khai_moc()
    {
        var handler = new CapturingHandler();
        await NewClient(handler).CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            new[] { "Q1" }, ScoringCriteriaBuilder.Build(new[] { Criterion(0, "Trống", 1.0m, 5) }),
            null, default);

        Assert.Empty(JsonNode.Parse(handler.CapturedBody!)!["criteria"]![0]!["levels"]!.AsArray());
    }

    // ── 🔴 HAI ĐƯỜNG RA PHẢI BẰNG NHAU (failure mode số 1) ────────────────

    // Chấm THẬT (payload gửi Interview) và chấm THỬ (payload gửi AIService) phải mang CÙNG MỘT bộ
    // thước đo. Trôi xa nhau ở đây là HR kiểm chứng thước A còn ứng viên bị chấm bằng thước B — và
    // KHÔNG có triệu chứng nào, vì cả hai đường vẫn trả điểm bình thường.
    [Fact]
    public void Cham_that_va_Cham_thu_gui_cung_mot_bo_moc_diem()
    {
        var rubric = SampleRubric();

        var chamThat = ScoringCriteriaBuilder.Build(rubric);
        var chamThu = RubricPreviewService.BuildPreviewCriteria(rubric);

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // So phần DÙNG CHUNG (tên/mô tả/trọng số/thang/mốc); chấm thử có thêm mức kỳ vọng, đó là phần
        // bọc bên ngoài chứ không phải một bản dựng lại.
        Assert.Equal(
            JsonSerializer.Serialize(chamThat.Select(c => new { c.Name, c.Description, c.Weight, c.MaxScore, c.Levels }), web),
            JsonSerializer.Serialize(chamThu.Select(c => new { c.Name, c.Description, c.Weight, c.MaxScore, c.Levels }), web));
    }

    // ── rubricVersion (CAMP-18) ──────────────────────────────────────────

    [Fact]
    public async Task Payload_gui_rubricVersion_theo_dung_khoa_camelCase()
    {
        var handler = new CapturingHandler();
        await NewClient(handler).CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            new[] { "Q1" }, ScoringCriteriaBuilder.Build(SampleRubric()),
            null, null, null, null, null, "Junior", rubricVersion: 7, null, default);

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.True(doc.RootElement.TryGetProperty("rubricVersion", out var v));
        Assert.Equal(7, v.GetInt32());
    }

    // Đường ngôn ngữ (overload đầy đủ ParticipationService dùng) cũng phải mang version — thiếu ở một
    // nhánh thì campaign tiếng Anh mất nhãn thước đo mà không nhánh nào báo.
    [Fact]
    public async Task Overload_day_du_cung_gui_rubricVersion()
    {
        var handler = new CapturingHandler();
        await NewClient(handler).CreateOrGetSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            new[] { "Q1" }, ScoringCriteriaBuilder.Build(SampleRubric()),
            null, null, null, null, null, "en", "Senior", 3, null, null, true, default);

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(3, doc.RootElement.GetProperty("rubricVersion").GetInt32());
    }
}
