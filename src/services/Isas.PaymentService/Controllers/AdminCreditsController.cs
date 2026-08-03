using System.Security.Claims;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Isas.Shared.Pagination;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Controllers
{
    /// <summary>
    /// F20 (vế Payment) — AUTH-7: PlatformAdmin cấp credit khuyến mãi và đọc ví của người khác.
    ///
    /// Trước vòng này KHÔNG có đường nào để admin chạm tới ví người khác: <c>me/account</c> và
    /// <c>me/credit-transactions</c> đều suy chủ ví từ JWT nên chỉ bao giờ nói về chính người gọi. Đây
    /// là lý do hai endpoint dưới phải tồn tại — và cũng là lý do chúng phải Admin-only tuyệt đối: chúng
    /// nhận <c>ownerId</c> từ CLIENT chứ không từ token.
    ///
    /// Route "admin" → gateway strip /api/v1/payment → /api/v1/payment/admin/credits/... (khớp
    /// admin/orders, admin/revenue, admin/invoices/close).
    /// </summary>
    [ApiController]
    [Route("admin")]
    [Authorize(Roles = "Admin")]
    public class AdminCreditsController : ControllerBase
    {
        private readonly PaymentDbContext _db;
        private readonly IAdminCreditService _credits;

        public AdminCreditsController(PaymentDbContext db, IAdminCreditService credits)
        {
            _db = db;
            _credits = credits;
        }

        // POST /payment/admin/credits/grant — cấp credit khuyến mãi.
        //
        // ⚠ Người cấp lấy từ JWT, KHÔNG nhận từ body: cấp credit là in tiền trong hệ thống này, để client
        // tự khai "tôi là admin nào" thì cột granted_by thành lời khai của chính kẻ cần bị truy trách nhiệm
        // (cùng lý lẽ với refunded_by ở F18).
        [HttpPost("credits/grant")]
        public async Task<ActionResult<GrantCreditResponse>> GrantCredits(
            [FromBody] GrantCreditRequest req, CancellationToken ct = default)
        {
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var adminId))
                return Forbid();

            var result = await _credits.GrantAsync(
                req.OwnerType!.Value, req.OwnerId!.Value, req.Credits, req.Note, req.IdempotencyKey, adminId, ct);

            return result.Outcome switch
            {
                GrantOutcome.InvalidAmount => BadRequest(new
                {
                    message = "`credits` phải lớn hơn 0. Muốn trừ credit thì dùng hoàn tiền đơn hàng "
                            + "(POST /admin/orders/{id}/refund) để có bút toán đảo gắn khoản gốc."
                }),
                GrantOutcome.WalletMissing => Conflict(new
                {
                    message = "Không cộng được vào ví (ví vừa thay đổi) — thử lại."
                }),
                _ => Ok(GrantCreditResponse.From(result))
            };
        }

        [HttpPost("credits/payment-mode")]
        public async Task<ActionResult<SetPaymentModeResponse>> SetPaymentMode(
            [FromBody] SetPaymentModeRequest req, CancellationToken ct = default)
        {
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var adminId))
                return Forbid();

            var result = await _credits.SetPaymentModeAsync(
                req.OwnerType!.Value, req.OwnerId!.Value, req.PaymentMode!.Value, req.CreditLimit,
                req.Note, req.AllowStrandedCredits, adminId, ct);

            return result.Outcome switch
            {
                SetPaymentModeOutcome.NotOrg => BadRequest(new
                {
                    message = "Chỉ Organization (B2B) đổi được payment mode — User (B2C) LUÔN Prepaid (D15)."
                }),
                SetPaymentModeOutcome.InvalidCreditLimit => BadRequest(new
                {
                    message = "Postpaid bắt buộc `creditLimit` > 0; Prepaid thì KHÔNG được truyền `creditLimit`."
                }),
                SetPaymentModeOutcome.PostpaidNotEligible => StatusCode(StatusCodes.Status403Forbidden, new
                {
                    message = "Tier B2B hiện hiệu lực không đủ điều kiện dùng Postpaid."
                }),
                SetPaymentModeOutcome.WalletMissing => NotFound(new
                {
                    message = "Chưa có ví cho chủ sở hữu này — không tự tạo ví qua endpoint duyệt."
                }),
                SetPaymentModeOutcome.StrandedCredits => Conflict(new
                {
                    message = "Ví còn credit đã mua (remaining/reserved > 0) sẽ KHÔNG dùng được sau khi " +
                               "chuyển Postpaid. Gọi lại với `allowStrandedCredits=true` nếu vẫn muốn tiếp tục.",
                    remainingCredits = result.RemainingCredits,
                    reservedCredits = result.ReservedCredits
                }),
                SetPaymentModeOutcome.UnpaidDebt => Conflict(new
                {
                    message = "Còn hóa đơn chưa tất toán (Issued/Overdue) hoặc kỳ hiện tại đã phát sinh " +
                               "sử dụng (period_usage > 0) — tất toán/đóng kỳ xong mới hạ về Prepaid."
                }),
                SetPaymentModeOutcome.Conflict => Conflict(new
                {
                    message = "Payment mode của ví vừa bị đổi bởi thao tác khác — tải lại và thử lại."
                }),
                _ => Ok(SetPaymentModeResponse.From(result))
            };
        }

        // GET /payment/admin/credits/{ownerType}/{ownerId} — số dư ví BẤT KỲ (đọc thuần, không tạo ví).
        // Ví chưa tồn tại → 200 với 0 credit (cùng quy ước me/account): "chưa có ví" là một sự thật hợp
        // lệ về chủ ví đó, không phải lỗi tra cứu.
        [HttpGet("credits/{ownerType}/{ownerId:guid}")]
        public async Task<ActionResult<CreditAccountResponse>> GetAccount(
            OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            var account = await _db.CreditAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId, ct);

            return account is null
                ? CreditAccountResponse.Empty(ownerType, ownerId)
                : CreditAccountResponse.ToResponse(account);
        }

        // GET /payment/admin/credits/{ownerType}/{ownerId}/transactions — sổ cái của ví BẤT KỲ.
        // Cùng hợp đồng keyset với `me/credit-transactions` (body mảng, X-Next-Cursor, default 500):
        // hỗ trợ hỗ-trợ-khách-hàng trả lời "credit của tôi đi đâu" mà không phải mở DB ra xem.
        [HttpGet("credits/{ownerType}/{ownerId:guid}/transactions")]
        public async Task<ActionResult<List<CreditTransactionResponse>>> GetTransactions(
            OwnerType ownerType, Guid ownerId,
            [FromQuery] CreditTransactionReason? reason = null,
            [FromQuery] string? cursor = null,
            [FromQuery] int? limit = null,
            CancellationToken ct = default)
        {
            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);

            var query = _db.CreditTransactions.AsNoTracking()
                .Where(t => t.OwnerType == ownerType && t.OwnerId == ownerId);

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

            // Bản admin trả kèm granted_by/note — ai cấp quà và vì sao là thứ admin cần thấy, còn chủ ví
            // thì không (đó là thông tin nội bộ về người vận hành).
            return Ok(rows.Select(CreditTransactionResponse.ToAdminResponse).ToList());
        }
    }
}
