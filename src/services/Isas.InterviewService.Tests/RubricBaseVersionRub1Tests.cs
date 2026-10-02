using System.Reflection;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// RUB1 · hợp đồng B + C — trang "Tiêu chí của tôi" biết bộ chuẩn đã cập nhật sau khi người luyện tuỳ
/// chỉnh: rubric riêng đóng dấu <c>basedOnDefaultVersion</c> lúc lưu, GET trả kèm <c>defaultVersion</c>
/// hiện tại, và <c>GET …/default</c> trả bộ chuẩn để FE so khác biệt.
/// </summary>
public class RubricBaseVersionRub1Tests
{
    private static readonly Guid Candidate = Guid.NewGuid();

    private static async Task SeedDefaultsAsync(TestDb t)
    {
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();
    }

    private static UpsertRubricRequest CloneOf(RubricResponse r)
        => new(r.Criteria.Select(c => new RubricCriterionInput(
            c.Name, c.Description, c.Weight, c.MaxScore,
            c.Levels.Select(l => new RubricLevelInput(l.Score, l.Descriptor)).ToList())).ToList());

    /// <summary>Admin đổi mô tả một tiêu chí ⇒ bộ chuẩn (BE, vi) lên một phiên bản.</summary>
    private static async Task<int> BumpDefaultAsync(TestDb t)
    {
        var admin = new AdminB2CRubricService(t.Db);
        var current = (await admin.GetAsync(JobCategory.BE, "vi"))!;
        var body = current.Criteria.Select(c => new AdminRubricCriterionInput(
            c.Id, c.Id == current.Criteria[0].Id ? $"{c.Description} (sửa {Guid.NewGuid():N})" : c.Description,
            c.Levels.Select(l => new AdminRubricLevelInput(l.Score, l.Descriptor)).ToList())).ToList();
        var bumped = (await admin.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;
        Assert.True(bumped.Changed);
        return bumped.Version;
    }

    [Fact]
    public async Task ChuaTuyChinh_TraBoChuan_DefaultVersion_BasedOnNull()
    {
        using var t = new TestDb();
        await SeedDefaultsAsync(t);
        var svc = new RubricLibraryService(t.Db);

        var r = await svc.GetEffectiveAsync(Candidate, JobCategory.BE);

        Assert.False(r.IsCustom);
        Assert.Equal(1, r.DefaultVersion);
        Assert.Null(r.BasedOnDefaultVersion);
    }

    /// <summary>
    /// Lưu rubric riêng ⇒ đóng dấu version bộ chuẩn LÚC LƯU. Admin bump sau đó ⇒ <c>defaultVersion</c> nhích
    /// lên còn <c>basedOnDefaultVersion</c> đứng yên — chính độ lệch đó là tín hiệu "bộ chuẩn đã cập nhật".
    /// </summary>
    [Fact]
    public async Task LuuRubricRieng_DongDauVersionBoChuan_AdminBumpSauDo_LechNhau()
    {
        using var t = new TestDb();
        await SeedDefaultsAsync(t);
        var v2 = await BumpDefaultAsync(t);
        var svc = new RubricLibraryService(t.Db);

        var saved = await svc.ReplaceAsync(
            Candidate, JobCategory.BE, CloneOf(await svc.GetEffectiveAsync(Candidate, JobCategory.BE)));
        Assert.True(saved.IsCustom);
        Assert.Equal(v2, saved.BasedOnDefaultVersion);
        Assert.Equal(v2, saved.DefaultVersion);
        Assert.All(
            await t.Db.RubricCriteria.AsNoTracking().Where(c => c.CandidateId == Candidate && c.IsActive).ToListAsync(),
            c => Assert.Equal(v2, c.BasedOnDefaultVersion));

        var v3 = await BumpDefaultAsync(t);
        var after = await svc.GetEffectiveAsync(Candidate, JobCategory.BE);

        Assert.True(after.IsCustom);
        Assert.Equal(v3, after.DefaultVersion);
        Assert.Equal(v2, after.BasedOnDefaultVersion);
        Assert.True(after.DefaultVersion > after.BasedOnDefaultVersion);
    }

    /// <summary>Rubric riêng lưu TRƯỚC RUB1 (cột null) ⇒ <c>basedOnDefaultVersion = null</c> — không suy thành v1.</summary>
    [Fact]
    public async Task RubricRiengCu_BasedOnNull_KhongSuyThanhV1()
    {
        using var t = new TestDb();
        await SeedDefaultsAsync(t);
        var svc = new RubricLibraryService(t.Db);
        await svc.ReplaceAsync(Candidate, JobCategory.BE,
            CloneOf(await svc.GetEffectiveAsync(Candidate, JobCategory.BE)));
        await t.Db.RubricCriteria.Where(c => c.CandidateId == Candidate)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.BasedOnDefaultVersion, (int?)null));

        var r = await svc.GetEffectiveAsync(Candidate, JobCategory.BE);

        Assert.True(r.IsCustom);
        Assert.Null(r.BasedOnDefaultVersion);
        Assert.Equal(1, r.DefaultVersion);
    }

    /// <summary>
    /// <c>GET …/default</c> trả BỘ CHUẨN kể cả khi người gọi đã có rubric riêng — đó là lý do endpoint tồn
    /// tại (GET hiệu lực khi đó trả rubric riêng, FE không có gì để so).
    /// </summary>
    [Fact]
    public async Task GetDefault_TraBoChuan_DuNguoiGoiCoRubricRieng()
    {
        using var t = new TestDb();
        await SeedDefaultsAsync(t);
        var svc = new RubricLibraryService(t.Db);
        var template = await svc.GetEffectiveAsync(Candidate, JobCategory.BE);
        var custom = CloneOf(template);
        custom.Criteria[0] = custom.Criteria[0] with { Name = "Tiêu chí của riêng tôi" };
        await svc.ReplaceAsync(Candidate, JobCategory.BE, custom);

        var r = await svc.GetDefaultAsync(JobCategory.BE);

        Assert.False(r.IsCustom);
        Assert.Null(r.BasedOnDefaultVersion);
        Assert.Equal(1, r.DefaultVersion);
        Assert.Equal(template.Criteria.Select(c => c.Name).OrderBy(n => n),
                     r.Criteria.Select(c => c.Name).OrderBy(n => n));
        Assert.DoesNotContain(r.Criteria, c => c.Name == "Tiêu chí của riêng tôi");
    }

    [Fact]
    public async Task GetDefault_ChuaCoBoChuan_200Rong_DefaultVersion0()
    {
        using var t = new TestDb();
        var r = await new RubricLibraryService(t.Db).GetDefaultAsync(JobCategory.BE);

        Assert.False(r.IsCustom);
        Assert.Empty(r.Criteria);
        Assert.Equal(0, r.DefaultVersion);
        Assert.Null(r.BasedOnDefaultVersion);
    }

    /// <summary>Endpoint C: route <c>{jobCategory}/default</c>, chỉ Candidate (kế thừa từ controller).</summary>
    [Fact]
    public void Controller_GetDefault_Route_VaRoleCandidate()
    {
        var method = typeof(RubricController).GetMethod(nameof(RubricController.GetDefault))!;
        Assert.Equal("{jobCategory}/default", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("Candidate", typeof(RubricController).GetCustomAttribute<AuthorizeAttribute>()!.Roles);
    }

    /// <summary>Ngôn ngữ sai ⇒ 400 qua controller (song ngữ chưa bật ⇒ "en" cũng 400 — cùng luật GET).</summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("")]
    public async Task Controller_GetDefault_NgonNguSai_400(string language)
    {
        using var t = new TestDb();
        await SeedDefaultsAsync(t);
        var controller = new RubricController(new RubricLibraryService(t.Db), NullLogger<RubricController>.Instance);

        var result = await controller.GetDefault(JobCategory.BE, language, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
