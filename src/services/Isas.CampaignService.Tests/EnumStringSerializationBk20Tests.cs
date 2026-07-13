using System.Text.Json;
using System.Text.Json.Serialization;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BK20 — Campaign phải nhận enum-as-string trong request body (như Interview), không chỉ numeric.
/// Bug bắt ở API sweep layer-3 (2026-07-13): gửi questions[].source="CustomHr" → 400 vì Campaign
/// chưa bật JsonStringEnumConverter. Unit test cũ gọi service trực tiếp (không qua model-binding) nên
/// không thấy. Test này chạy đúng cấu hình JSON của Program.cs (BK20) để khoá hành vi.
/// </summary>
public class EnumStringSerializationBk20Tests
{
    // Mirror chính xác AddJsonOptions trong Program.cs (BK20): converter enum-as-string.
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        // ASP.NET Core MVC khởi tạo JsonOptions từ JsonSerializerDefaults.Web (camelCase +
        // case-insensitive); AddJsonOptions chỉ thêm converter lên trên. Mirror đúng để test
        // phản ánh pipeline model-binding thật.
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    [Fact]
    public void QuestionItem_Source_accepts_string_enum()
    {
        var item = JsonSerializer.Deserialize<QuestionItem>(
            """{ "questionText": "Explain SOLID", "source": "CustomHr", "isRequired": true }""",
            Options);

        Assert.NotNull(item);
        Assert.Equal(QuestionSource.CustomHr, item!.Source);
    }

    [Fact]
    public void QuestionItem_Source_still_accepts_numeric_enum_backward_compat()
    {
        // JsonStringEnumConverter đọc được cả số → không phá client cũ gửi 0/1.
        var item = JsonSerializer.Deserialize<QuestionItem>(
            """{ "questionText": "Explain SOLID", "source": 1, "isRequired": true }""",
            Options);

        Assert.NotNull(item);
        Assert.Equal(QuestionSource.CustomHr, item!.Source);
    }

    [Fact]
    public void TransitionStatusRequest_accepts_string_enum()
    {
        var req = JsonSerializer.Deserialize<TransitionStatusRequest>(
            """{ "status": "Closed" }""", Options);

        Assert.NotNull(req);
        Assert.Equal(CampaignStatus.Closed, req!.Status);
    }

    [Fact]
    public void Response_enums_serialize_as_string()
    {
        // Response hiện .ToString() sẵn → converter không đổi output; test khoá không hồi quy.
        var json = JsonSerializer.Serialize(
            new CampaignQuestionResponse { Source = QuestionSource.CustomHr.ToString() }, Options);

        Assert.Contains("\"CustomHr\"", json);
    }
}
