using System.Security.Claims;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;

namespace Isas.PaymentService.Controllers;

/// <summary>
/// Bảng giá gói phân tầng cho NGƯỜI MUA (B2C lẫn B2B).
///
/// Trước endpoint này, catalog gói chỉ có ở <see cref="PlanController"/> — <c>[Authorize(Roles="Admin")]</c>
/// — nên không candidate/employer nào xem được mình đang ở gói gì hay có thể nâng lên gì. Đó là đúng lỗ mà
/// <c>GET /me/account</c> đã phải vá cho số dư credit (e2e 2026-07-18): tính năng chạy đủ dưới BE nhưng
/// không có đường nào lộ ra người dùng.
/// </summary>
[ApiController]
[Route("plans")]
public sealed class PublicPlanController(PlanCatalogService catalog) : ControllerBase
{
    /// <summary>Bảng giá — PUBLIC như <c>GET /package</c> (payment.md:104): xem giá không cần đăng nhập.</summary>
    [HttpGet]
    [AllowAnonymous]
    public Task<List<PublicPlanResponse>> GetCatalogAsync(
        [FromQuery] PlanAudience? audience, CancellationToken ct = default)
        => catalog.GetCatalogAsync(audience, ct);

    /// <summary>
    /// Gói đang dùng + hạn mức còn lại của chính người gọi.
    ///
    /// A5 — chỉ cần đăng nhập: chủ ví suy từ JWT (D15), không nhận tham số ⇒ không có đường đọc gói của
    /// người khác. HrMember XEM được gói của org (AUTH-6 chỉ chặn money-mutation) — cùng lối
    /// <c>GET /me/account</c>, <c>GET /me/subscription</c>.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MyPlanResponse>> GetMyPlanAsync(CancellationToken ct = default)
    {
        var owner = GetOwner();
        if (owner is null) return Forbid();
        return await catalog.GetMyPlanAsync(owner.Value.OwnerType, owner.Value.OwnerId, ct);
    }

    // Chủ ví lấy từ JWT (D15) — sao y CreditAccountController/SubscriptionController: có org_id ⇒ ví ORG
    // (AUTH-8: billing gắn theo org, không theo cá nhân HR), không thì ví User.
    private (OwnerType OwnerType, Guid OwnerId)? GetOwner()
    {
        var orgId = User.FindFirstValue("org_id");
        if (!string.IsNullOrWhiteSpace(orgId) && Guid.TryParse(orgId, out var oid))
            return (OwnerType.Org, oid);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId) && Guid.TryParse(userId, out var uid))
            return (OwnerType.User, uid);

        return null;
    }
}
