using System.Text.Json;
using System.Text.Json.Serialization;
using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Regression cho N7: FE gửi requirement dưới dạng đúng `{ "text": "..." }`, không gửi ID.
/// Test service/controller trực tiếp với <c>new CvRequirementInput(null, text)</c> không phủ JSON
/// constructor binding và từng để OpenAPI production đánh dấu <c>requirementId</c> là required.
/// </summary>
public class CvAnalysisWireContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        // Khớp semantics mà ASP.NET/OpenAPI dùng để phân biệt constructor parameter bắt buộc với
        // parameter có default. Bật tường minh để regression không phụ thuộc feature-switch máy test.
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void AnalyzeRequest_RequirementIdBiBoHan_DeserializeThanhNull()
    {
        const string json = """
            {
              "cvId": "11111111-1111-1111-1111-111111111111",
              "jdId": null,
              "jobCategory": "BE",
              "jdText": "Backend developer",
              "mustHave": [{ "text": "ASP.NET Core" }],
              "niceToHave": [{ "text": "Kubernetes" }]
            }
            """;

        var request = JsonSerializer.Deserialize<CvAnalysisRequest>(json, WebJson);

        Assert.NotNull(request);
        var must = Assert.Single(request.MustHave!);
        Assert.Equal("ASP.NET Core", must.Text);
        Assert.Null(must.RequirementId);
        var nice = Assert.Single(request.NiceToHave!);
        Assert.Equal("Kubernetes", nice.Text);
        Assert.Null(nice.RequirementId);
    }

    [Fact]
    public void RequirementId_CoDefaultNull_DeOpenApiKhongDanhDauRequired()
    {
        var constructor = Assert.Single(typeof(CvRequirementInput).GetConstructors());
        var requirementId = Assert.Single(constructor.GetParameters(),
            parameter => parameter.Name == "RequirementId");

        Assert.True(requirementId.HasDefaultValue);
        Assert.Null(requirementId.DefaultValue);
    }
}
