using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using System.Security.Claims;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// F8 — `GET /payment/me/subscription`: kỳ hạn thuê bao của chính người gọi.
    ///
    /// Không có endpoint này thì người vừa mua gói tháng không có cách nào biết mình đang có quyền gì
    /// (đúng lỗ mà `GET /me/account` đã phải vá cho số dư credit ở e2e 2026-07-18). FE cần nó để hiện
    /// trạng thái "Premium tới ngày …" và để quyết định hiện nút Mua hay nút Gia hạn.
    ///
    /// Đọc THUẦN — không tạo/không sửa gì.
    /// </summary>
    [ApiController]
    public class SubscriptionController : ControllerBase
    {
        private readonly ISubscriptionService _subscriptions;

        public SubscriptionController(ISubscriptionService subscriptions)
        {
            _subscriptions = subscriptions;
        }

        // Chủ ví lấy từ JWT (D15) — đồng nhất với CreditAccountController/OrderController.
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

        // A5 — chỉ cần đăng nhập: thuê bao là của chính caller (owner suy từ JWT, không nhận tham số) nên
        // không có đường đọc thuê bao người khác. HrMember XEM được membership của org (AUTH-6 chỉ chặn
        // money-mutation) — cùng lối GET /me/account, GET /me/invoices.
        [HttpGet("me/subscription")]
        [Authorize]
        public async Task<ActionResult<SubscriptionResponse>> GetMySubscriptionAsync(CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null) return Forbid();

            var sub = await _subscriptions.GetActiveAsync(owner.Value.OwnerType, owner.Value.OwnerId, ct);

            // Không có thuê bao KHÔNG phải lỗi — trả trạng thái "không có" để FE khỏi phải phân biệt
            // 404-vì-chưa-mua với 404-vì-route-sai (cùng lối CreditAccountResponse.Empty).
            return sub is null
                ? SubscriptionResponse.None(owner.Value.OwnerType, owner.Value.OwnerId)
                : SubscriptionResponse.ToResponse(sub);
        }
    }

    /// <param name="Active">Có quyền unlimited tại thời điểm gọi hay không — FE chỉ cần đọc cờ này.</param>
    public record SubscriptionResponse(
        OwnerType OwnerType,
        Guid OwnerId,
        bool Active,
        string? BillingCycle,
        DateTime? StartedAt,
        DateTime? ExpiresAt)
    {
        public static SubscriptionResponse None(OwnerType ownerType, Guid ownerId) =>
            new(ownerType, ownerId, false, null, null, null);

        public static SubscriptionResponse ToResponse(Subscription s) =>
            new(s.OwnerType, s.OwnerId, true, s.BillingCycle.ToString(), s.StartedAt, s.ExpiresAt);
    }
}
