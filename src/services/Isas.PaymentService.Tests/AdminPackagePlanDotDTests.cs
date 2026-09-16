using System.Security.Claims;
using Isas.PaymentService.Controllers;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Đợt D màn admin (2026-09-16) — ba sửa nhỏ để màn Gói &amp; Tier không đẻ ra bẫy:
/// BE-D1 <c>?includeInactive=true</c> chỉ Admin (gói đã ẩn phải thấy lại được để "Bán lại", nhưng public
/// KHÔNG được thấy giá chưa công bố) · BE-D2 ngừng bán gói mặc định trả 400 thay 500 · BE-D3
/// <c>PlanResponse</c> mang <c>EntitlementsJson</c> để FE echo khi PUT (ApplyTo luôn ghi đè).
/// </summary>
public class AdminPackagePlanDotDTests
{
    private static async Task<(ProductPackage active, ProductPackage hidden)> SeedAsync(PaymentTestDb tdb)
    {
        var active = new ProductPackage { Id = Guid.NewGuid(), Name = "Gói đang bán", Type = PackageType.OneTime, PriceVnd = 2000, InterviewCredits = 5, IsActive = true, CreatedAt = DateTime.UtcNow.AddMinutes(-2) };
        var hidden = new ProductPackage { Id = Guid.NewGuid(), Name = "Gói đã ẩn", Type = PackageType.OneTime, PriceVnd = 9000, InterviewCredits = 50, IsActive = false, CreatedAt = DateTime.UtcNow.AddMinutes(-1) };
        tdb.Db.ProductPackages.AddRange(active, hidden);
        await tdb.Db.SaveChangesAsync();
        return (active, hidden);
    }

    private static PackageController ControllerAs(PaymentTestDb tdb, params string[] roles)
    {
        var controller = new PackageController(new PackageService(NullLogger<PackageService>.Instance, tdb.Db));
        var identity = new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), roles.Length > 0 ? "Test" : null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return controller;
    }

    [Fact]
    public async Task Service_IncludeInactive_TraCaGoiDaAn_MacDinhChiGoiDangBan()
    {
        using var tdb = new PaymentTestDb();
        var (active, hidden) = await SeedAsync(tdb);
        var service = new PackageService(NullLogger<PackageService>.Instance, tdb.Db);

        var publicList = await service.GetAllPackagesAsync(CancellationToken.None);
        var adminList = await service.GetAllPackagesAsync(includeInactive: true, CancellationToken.None);

        Assert.Equal([active.Id], publicList.Select(p => p.Id));
        Assert.Equal([active.Id, hidden.Id], adminList.Select(p => p.Id));
        Assert.Null(await service.GetPackageAsync(hidden.Id, CancellationToken.None));
        Assert.Equal(hidden.Id, (await service.GetPackageAsync(hidden.Id, includeInactive: true, CancellationToken.None))!.Id);
    }

    // Vế bảo mật: người lạ truyền ?includeInactive=true vẫn chỉ thấy gói đang bán — cờ chỉ có nghĩa với Admin.
    [Fact]
    public async Task Controller_AnDanh_TruyenIncludeInactive_VanChiThayGoiDangBan()
    {
        using var tdb = new PaymentTestDb();
        var (active, hidden) = await SeedAsync(tdb);

        var anonymous = await ControllerAs(tdb).GetAllPackageAsync(includeInactive: true);
        var candidate = await ControllerAs(tdb, "Candidate").GetAllPackageAsync(includeInactive: true);
        var admin = await ControllerAs(tdb, "Admin").GetAllPackageAsync(includeInactive: true);
        var adminDefault = await ControllerAs(tdb, "Admin").GetAllPackageAsync();

        Assert.Equal([active.Id], anonymous.Value!.Select(p => p.Id));
        Assert.Equal([active.Id], candidate.Value!.Select(p => p.Id));
        Assert.Equal([active.Id, hidden.Id], admin.Value!.Select(p => p.Id));
        Assert.Equal([active.Id], adminDefault.Value!.Select(p => p.Id));

        var byIdAnonymous = await ControllerAs(tdb).GetPackageAsync(hidden.Id, includeInactive: true);
        Assert.IsType<NotFoundObjectResult>(byIdAnonymous.Result);
        var byIdAdmin = await ControllerAs(tdb, "Admin").GetPackageAsync(hidden.Id, includeInactive: true);
        Assert.Equal(hidden.Id, byIdAdmin.Value!.Id);
    }

    // BE-D2: trước đây ArgumentException của PlanService.DeactivateAsync thoát controller ⇒ 500.
    [Fact]
    public async Task Deactivate_GoiMacDinh_400KemLyDo_KhongPhai500()
    {
        using var tdb = new PaymentTestDb();
        var free = await tdb.Db.Plans.SingleAsync(p => p.Audience == PlanAudience.B2C && p.Code == "free");
        var controller = new PlanController(new PlanService(tdb.Db, Options.Create(new TieringSettings())));

        var result = await controller.DeactivateAsync(free.Id, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("default plan", bad.Value!.ToString());
        Assert.True((await tdb.Db.Plans.AsNoTracking().SingleAsync(p => p.Id == free.Id)).IsActive);
    }

    [Fact]
    public async Task Deactivate_GoiThuong_204_VaHaCo()
    {
        using var tdb = new PaymentTestDb();
        var plus = await tdb.Db.Plans.SingleAsync(p => p.Audience == PlanAudience.B2C && p.Code == "plus");
        var controller = new PlanController(new PlanService(tdb.Db, Options.Create(new TieringSettings())));

        Assert.IsType<NoContentResult>(await controller.DeactivateAsync(plus.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.DeactivateAsync(Guid.NewGuid(), CancellationToken.None));
    }

    // BE-D3: FE echo lại đúng giá trị này khi PUT — thiếu nó, mỗi lần Sửa là JSON về "[]" im lặng.
    [Fact]
    public void PlanResponse_From_MangEntitlementsJson()
    {
        var plan = new Plan { Id = Guid.NewGuid(), Audience = PlanAudience.B2B, Code = "biz", Name = "Business", EntitlementsJson = "[{\"k\":\"seats\",\"v\":5}]", EntitlementsVersion = 3 };

        var response = PlanResponse.From(plan);

        Assert.Equal("[{\"k\":\"seats\",\"v\":5}]", response.EntitlementsJson);
        Assert.Equal(3, response.EntitlementsVersion);
    }
}
