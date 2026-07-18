using System.Security.Claims;
using Isas.PaymentService.Controllers;
using Isas.PaymentService.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// `GET /payment/me/account` (payment.md:120) — số dư ví của chính caller.
/// Bắt ở e2e 2026-07-18: endpoint chưa build ⇒ không màn hình nào hiện được số dư credit.
///
/// Khoá 3 điều: owner suy từ JWT theo D15 (org_id → Org, else sub → User) nên không đọc được ví
/// người khác; ví chưa tồn tại → 0 credit chứ không 404; ví có thật → trả đúng số dư.
/// </summary>
public class CreditAccountMeTests
{
    private static CreditAccountController NewController(PaymentTestDb tdb, params Claim[] claims)
    {
        var controller = new CreditAccountController(tdb.Db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                },
            },
        };
        return controller;
    }

    private static async Task SeedAccountAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining, int reserved)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = reserved,
            UpdatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();
    }

    // B2C: không có claim org_id → ví User = sub.
    [Fact]
    public async Task B2C_TraSoDuViCuaChinhUser()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 14, reserved: 1);
        var controller = NewController(tdb, new Claim(ClaimTypes.NameIdentifier, userId.ToString()));

        var result = await controller.GetMyAccountAsync(CancellationToken.None);

        var account = Assert.IsType<CreditAccountResponse>(result.Value);
        Assert.Equal(OwnerType.User, account.OwnerType);
        Assert.Equal(userId, account.OwnerId);
        Assert.Equal(14, account.RemainingCredits);
        Assert.Equal(1, account.ReservedCredits);
    }

    // B2B (D15): có claim org_id → ví ORG, KHÔNG phải ví cá nhân người gọi.
    [Fact]
    public async Task B2B_CoOrgId_TraViOrg_KhongPhaiViCaNhan()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 20, reserved: 5);
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 999, reserved: 0);
        var controller = NewController(
            tdb,
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("org_id", orgId.ToString()));

        var result = await controller.GetMyAccountAsync(CancellationToken.None);

        var account = Assert.IsType<CreditAccountResponse>(result.Value);
        Assert.Equal(OwnerType.Org, account.OwnerType);
        Assert.Equal(orgId, account.OwnerId);
        Assert.Equal(20, account.RemainingCredits);
    }

    // Chưa từng mua credit ⇒ chưa có row ví → 0 credit, KHÔNG 404 (FE hiện "0" thay vì màn hình lỗi).
    [Fact]
    public async Task ChuaCoVi_Tra0Credit_Khong404()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var controller = NewController(tdb, new Claim(ClaimTypes.NameIdentifier, userId.ToString()));

        var result = await controller.GetMyAccountAsync(CancellationToken.None);

        var account = Assert.IsType<CreditAccountResponse>(result.Value);
        Assert.Equal(0, account.RemainingCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Equal(PaymentMode.Prepaid, account.PaymentMode);
        Assert.Equal(CreditAccountStatus.Active, account.Status);
        // Đọc thuần — KHÔNG được tự tạo ví trong DB.
        Assert.Empty(tdb.Db.CreditAccounts);
    }

    // Token không mang sub lẫn org_id → không xác định được chủ ví.
    [Fact]
    public async Task KhongCoClaimChuVi_TraForbid()
    {
        using var tdb = new PaymentTestDb();
        var controller = NewController(tdb);

        var result = await controller.GetMyAccountAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }
}
