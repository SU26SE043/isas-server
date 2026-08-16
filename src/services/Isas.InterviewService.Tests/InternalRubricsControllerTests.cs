using System.Reflection;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// <c>GET /internal/rubrics/b2c</c> — CampaignService kéo bộ chuẩn về làm "bộ mặc định theo nghề".
///
/// <para>GEN-1: internal, không qua gateway, gác bằng <c>X-Internal-Token</c>. GEN-2: Campaign KHÔNG
/// đọc DB của Interview, nó CHÉP nội dung về bảng của chính nó.</para>
/// </summary>
public class InternalRubricsControllerTests
{
    private const string Token = "internal-secret";

    private static InternalRubricsController Controller(TestDb t, string? configuredToken = Token)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configuredToken is null
                ? []
                : new Dictionary<string, string?> { ["Internal:Token"] = configuredToken })
            .Build();
        return new InternalRubricsController(t.Db, config, NullLogger<InternalRubricsController>.Instance);
    }

    private static async Task SeedWithLevelsAsync(TestDb t)
    {
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();

        var admin = new AdminB2CRubricService(t.Db);
        var v1 = (await admin.GetAsync(JobCategory.BE, "vi"))!;
        await admin.ReplaceAsync(JobCategory.BE, new UpsertAdminRubricRequest(
            v1.Criteria.Select(c => new AdminRubricCriterionInput(c.Id, c.Description,
            [
                new(0, "Không nêu được ý nào liên quan tới câu hỏi, hoặc bỏ trống."),
                new(5, "Nêu ý chính, có ví dụ từ dự án thật và chỉ ra được đánh đổi của phương án.")
            ])).ToList()), "vi");
    }

    private static IReadOnlyList<object> CriteriaOf(object body)
    {
        var criteria = body.GetType().GetProperty("criteria")!.GetValue(body)!;
        return ((System.Collections.IEnumerable)criteria).Cast<object>().ToList();
    }

    private static T Read<T>(object item, string name)
        => (T)item.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(item)!;

    [Fact]
    public async Task Get_WrongToken_Unauthorized()
    {
        using var t = new TestDb();
        await SeedWithLevelsAsync(t);
        var result = await Controller(t).GetB2CDefaultAsync("sai", JobCategory.BE, "vi", default);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>Fail-closed: chưa cấu hình token thì từ chối hết, KHÔNG mở toang.</summary>
    [Fact]
    public async Task Get_TokenNotConfigured_Unauthorized()
    {
        using var t = new TestDb();
        await SeedWithLevelsAsync(t);
        var result = await Controller(t, configuredToken: null)
            .GetB2CDefaultAsync(Token, JobCategory.BE, "vi", default);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Get_NoActiveSet_NotFound()
    {
        using var t = new TestDb();
        var result = await Controller(t).GetB2CDefaultAsync(Token, JobCategory.BE, "vi", default);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Get_UnknownLanguage_BadRequest()
    {
        using var t = new TestDb();
        await SeedWithLevelsAsync(t);
        var result = await Controller(t).GetB2CDefaultAsync(Token, JobCategory.BE, "fr", default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Get_ReturnsActiveSetWithLevels()
    {
        using var t = new TestDb();
        await SeedWithLevelsAsync(t);

        var ok = Assert.IsType<OkObjectResult>(
            await Controller(t).GetB2CDefaultAsync(Token, JobCategory.BE, "vi", default));
        var body = ok.Value!;

        Assert.Equal("BE", Read<string>(body, "jobCategory"));
        Assert.Equal("vi", Read<string>(body, "language"));
        Assert.Equal(2, Read<int>(body, "version"));   // đã bump khi khai mốc

        var criteria = CriteriaOf(body);
        Assert.Equal(7, criteria.Count);
        foreach (var c in criteria)
        {
            Assert.False(string.IsNullOrWhiteSpace(Read<string>(c, "name")));
            Assert.True(Read<int>(c, "maxScore") > 0);
            var levels = ((System.Collections.IEnumerable)c.GetType().GetProperty("levels")!.GetValue(c)!)
                .Cast<object>().ToList();
            Assert.Equal(2, levels.Count);
            // Mốc phải TĂNG DẦN — `.Include()` không bảo đảm thứ tự nào cả.
            Assert.Equal([0, 5], levels.Select(l => Read<int>(l, "score")).ToArray());
        }
    }

    /// <summary>
    /// Hợp đồng CỐ Ý không mang <c>id</c> (vô nghĩa với Campaign — nó mint id riêng khi chép) và
    /// không mang <c>scoringScope</c> (Campaign không có cột đó; đường chấm B2B chấm mọi tiêu chí ở
    /// mọi câu, nên thêm cột mà không ai đọc là một cột nói dối).
    /// </summary>
    [Theory]
    [InlineData("id")]
    [InlineData("criterionId")]
    [InlineData("scoringScope")]
    public async Task Get_DoesNotLeakInternalOnlyFields(string field)
    {
        using var t = new TestDb();
        await SeedWithLevelsAsync(t);

        var ok = Assert.IsType<OkObjectResult>(
            await Controller(t).GetB2CDefaultAsync(Token, JobCategory.BE, "vi", default));

        var first = CriteriaOf(ok.Value!)[0];
        Assert.Null(first.GetType().GetProperty(field));
    }

    /// <summary>Bộ chưa khai mốc vẫn trả về (mốc rỗng = trạng thái hợp lệ, không phải lỗi).</summary>
    [Fact]
    public async Task Get_SetWithoutLevels_ReturnsEmptyLevels()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(
            await Controller(t).GetB2CDefaultAsync(Token, JobCategory.FE, "en", default));

        var criteria = CriteriaOf(ok.Value!);
        Assert.Equal(7, criteria.Count);
        Assert.All(criteria, c => Assert.Empty(
            ((System.Collections.IEnumerable)c.GetType().GetProperty("levels")!.GetValue(c)!).Cast<object>()));
    }

    /// <summary>Rubric RIÊNG của ứng viên không được lọt vào bộ mặc định gửi sang Campaign.</summary>
    [Fact]
    public async Task Get_ExcludesCandidateOwnedRubric()
    {
        using var t = new TestDb();
        await SeedWithLevelsAsync(t);
        t.Db.RubricCriteria.Add(TestDb.Criterion(JobCategory.BE, version: 1, active: true,
            name: "Tiêu chí riêng của ứng viên", candidateId: Guid.NewGuid()));
        await t.Db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(
            await Controller(t).GetB2CDefaultAsync(Token, JobCategory.BE, "vi", default));

        Assert.DoesNotContain(CriteriaOf(ok.Value!),
            c => Read<string>(c, "name") == "Tiêu chí riêng của ứng viên");
    }
}
