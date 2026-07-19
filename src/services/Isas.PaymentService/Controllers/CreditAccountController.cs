using Isas.PaymentService.DTOs;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;
using System.Security.Claims;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// payment.md:120 — `GET /payment/me/account`: số dư ví của chính người gọi.
    ///
    /// Bắt ở e2e 2026-07-18: KHÔNG có endpoint nào đọc được số dư ⇒ cả trang Credit của Candidate lẫn
    /// của Employer chỉ hiện gói bán + lịch sử đơn, người dùng vừa trả tiền xong không biết mình còn
    /// bao nhiêu credit. Ví chỉ quan sát được qua DB.
    ///
    /// Đọc THUẦN (không tạo ví, không ghi DB): ví thật được tạo lazy ở luồng webhook Paid (P2).
    /// </summary>
    [ApiController]
    public class CreditAccountController : ControllerBase
    {
        private readonly PaymentDbContext _db;

        public CreditAccountController(PaymentDbContext db)
        {
            _db = db;
        }

        // Chủ ví lấy từ JWT (D15) — giống Order/InvoiceController: claim org_id → Org (B2B), không → User (B2C).
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

        // A5 — chỉ cần đăng nhập: ví là của chính caller (owner suy từ JWT, không nhận tham số) nên
        // không có đường đọc ví người khác. HrMember XEM được số dư org (AUTH-6 chỉ chặn money-mutation,
        // đọc billing vẫn cho — cùng lối GET /me/invoices).
        [HttpGet("me/account")]
        [Authorize]
        public async Task<ActionResult<CreditAccountResponse>> GetMyAccountAsync(CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null) return Forbid();

            var account = await _db.CreditAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    a => a.OwnerType == owner.Value.OwnerType && a.OwnerId == owner.Value.OwnerId, ct);

            // Chưa có ví = chưa từng mua credit → 0 credit, không phải lỗi (payment.md:120 chỉ liệt kê 401).
            return account is null
                ? CreditAccountResponse.Empty(owner.Value.OwnerType, owner.Value.OwnerId)
                : CreditAccountResponse.ToResponse(account);
        }

        // F19 — GET /payment/me/credit-transactions: lịch sử BIẾN ĐỘNG credit của chính người gọi.
        // `me/account` cho biết còn bao nhiêu, endpoint này cho biết vì sao — trước vòng này không ai
        // (kể cả chủ ví) đọc được `credit_transactions`, nên mất credit là mất không dấu vết.
        //
        // Keyset-paged theo mẫu chung (DB8): body vẫn là MẢNG JSON, `?cursor=&limit=` opt-in, next-cursor
        // ở header X-Next-Cursor, default limit 500 ⇒ hợp đồng không đổi với client cũ.
        // ?reason= lọc theo loại bút toán (Purchase/Consume/Refund/FreeGrant/PromoGrant).
        //
        // ⚠ Vị ngữ chủ ví là VÔ ĐIỀU KIỆN và đứng TRƯỚC cursor: cursor chỉ mang (created_at, id) chứ
        // KHÔNG mang owner, nên bỏ nó ra là cursor dẫn thẳng sang sổ cái của người khác (cùng cảnh báo
        // đã ghi ở GetOwnerOrdersAsync).
        [HttpGet("me/credit-transactions")]
        [Authorize]
        public async Task<ActionResult<List<CreditTransactionResponse>>> GetMyCreditTransactionsAsync(
            [FromQuery] CreditTransactionReason? reason = null,
            [FromQuery] string? cursor = null,
            [FromQuery] int? limit = null,
            CancellationToken ct = default)
        {
            var owner = GetOwner();
            if (owner is null) return Forbid();

            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);

            var query = _db.CreditTransactions.AsNoTracking()
                .Where(t => t.OwnerType == owner.Value.OwnerType && t.OwnerId == owner.Value.OwnerId);

            if (reason is CreditTransactionReason r)
                query = query.Where(t => t.Reason == r);
            if (cur is not null)
                query = query.Where(t => t.CreatedAt < cur.CreatedAt
                    || (t.CreatedAt == cur.CreatedAt && t.Id.CompareTo(cur.Id) < 0));

            var rows = await query
                .OrderByDescending(t => t.CreatedAt)
                .ThenByDescending(t => t.Id)
                .Take(take)
                .ToListAsync(ct);

            if (rows.Count == take)
                Response.Headers[KeysetPaging.NextCursorHeader] =
                    new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode();

            return Ok(rows.Select(CreditTransactionResponse.ToResponse).ToList());
        }
    }
}
