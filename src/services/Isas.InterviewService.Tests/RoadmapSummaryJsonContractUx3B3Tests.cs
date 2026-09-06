using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.Shared.Json;

namespace Isas.InterviewService.Tests;

/// <summary>
/// UX3-B3 — LƯỚI CHẶN LỆCH HỢP ĐỒNG cho <see cref="RoadmapSummaryResponse"/> (GET /practice/roadmaps).
///
/// <para>Đợt rà UX3 tìm được &gt;30 trường frontend đọc mà backend không gửi (vd roadmap list: FE
/// fallback <c>updatedAt = new Date()</c> ⇒ mọi thẻ hiện NGÀY HÔM NAY và danh sách sắp ngẫu nhiên).
/// KHÔNG lỗi nào nổ. Test này biến việc ĐỔI TÊN một trường thành một test ĐỎ ngay tại backend.</para>
///
/// <para>Chỉ khoá TẬP TÊN KHOÁ CẤP MỘT — không kiểu, không giá trị. Danh sách kỳ vọng là chuỗi
/// VIẾT CỨNG (không <c>nameof</c>, không reflection).</para>
/// </summary>
public class RoadmapSummaryJsonContractUx3B3Tests
{
    // Mirror của Program.cs:251 `AddControllers().AddJsonOptions(...)`. ASP.NET MVC khởi tạo
    // JsonOptions.JsonSerializerOptions = new(JsonSerializerDefaults.Web) ⇒ camelCase; Interview thêm
    // JsonStringEnumConverter + UtcDateTimeConverter (đổi GIÁ TRỊ, không đổi KHOÁ).
    // KHÔNG dựng `new JsonSerializerOptions()` trần: sẽ ra PascalCase và bỏ sót đúng lớp bug lưới này canh.
    private static JsonSerializerOptions RuntimeOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new UtcDateTimeConverter());
        return o;
    }

    // ── HỢP ĐỒNG: tên khoá JSON cấp một của RoadmapSummaryResponse. Sắp xếp. Chuỗi VIẾT CỨNG.
    //    ⚠ ĐỔI DANH SÁCH NÀY = ĐỔI HỢP ĐỒNG VỚI FRONTEND.
    private static readonly string[] ExpectedKeys =
    [
        "completedAt",
        "createdAt",
        "cvId",
        "hasFinalReport",
        "id",
        "jobCategory",
        "level",
        "milestoneCount",
        "milestoneDoneCount",
        "mode",
        "name",
        "status",
    ];

    [Fact]
    public void RoadmapSummaryResponse_TopLevelJsonKeys_MatchFrozenContract()
    {
        var sample = new RoadmapSummaryResponse(
            Id: Guid.Empty,
            Name: "",
            JobCategory: "",
            Level: "",
            Mode: "",
            CvId: null,
            Status: "",
            CreatedAt: default,
            CompletedAt: null,
            HasFinalReport: false,
            MilestoneCount: 0,
            MilestoneDoneCount: 0);

        var json = JsonSerializer.Serialize(sample, RuntimeOptions());
        var actual = ((JsonObject)JsonNode.Parse(json)!)
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(
            actual.SequenceEqual(ExpectedKeys),
            "Tên khoá JSON của RoadmapSummaryResponse đã lệch hợp đồng.\n" +
            "Đổi tên khoá JSON là ĐỔI HỢP ĐỒNG. Nếu cố ý, cập nhật ExpectedKeys trong test NÀY " +
            "VÀ báo cho bên frontend (họ đang đọc đúng những tên cũ).\n\n" +
            "Kỳ vọng : [" + string.Join(", ", ExpectedKeys) + "]\n" +
            "Thực tế : [" + string.Join(", ", actual) + "]\n\n" +
            "Danh sách mới (dán vào ExpectedKeys nếu ĐÚNG Ý ĐỒ):\n" +
            string.Join("\n", actual.Select(k => $"        \"{k}\",")));
    }
}
