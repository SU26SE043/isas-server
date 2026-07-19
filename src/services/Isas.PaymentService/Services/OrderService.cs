using Isas.PaymentService.Models;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PayOS;
using PayOS.Models.V2.PaymentRequests;
using static Isas.PaymentService.DTOs.OrderRequest;

namespace Isas.PaymentService.Services
{
    public class OrderService : IOrderService
    {
        private readonly PaymentDbContext _db;
        private readonly PayOSClient _payos;
        private readonly IOptions<PayOSSettings> _settings;
        private readonly IOrderCodeGenerator _orderCodes;

        public OrderService(PaymentDbContext db, PayOSClient payos, IOptions<PayOSSettings> settings,
            IOrderCodeGenerator orderCodes)
        {
            _db = db;
            _payos = payos;
            _settings = settings;
            _orderCodes = orderCodes;
        }

        public async Task<OrderResponse> CreateOrderAsync(OwnerType ownerType, Guid ownerId, CreateOrderRequest request, CancellationToken ct = default)
        {
            // 1. Fetch package
            var package = await _db.ProductPackages.FirstOrDefaultAsync(p => p.Id == request.PackageId, ct)
                ?? throw new KeyNotFoundException("Package not found.");

            if (!package.IsActive)
                throw new InvalidOperationException("Package is no longer available.");

            // F8 — gói thuê bao đi ĐƯỜNG RIÊNG, KHÔNG phải đường CreditPack. Đây là điểm mấu chốt để
            // KHÔNG phải gỡ guard DB20 ngay bên dưới: bất biến "Kind=CreditPack ⇒ gói sinh credit > 0"
            // được giữ nguyên vẹn, gói thuê bao chỉ đơn giản không bao giờ mang Kind đó nữa.
            // (Trước F8 nhánh này không tồn tại nên gói Subscription hiện trong catalog mà bấm Mua là 400 —
            // bẫy đã ghi ở F25.)
            if (package.Type == PackageType.Subscription)
                return await CreateSubscriptionOrderAsync(ownerType, ownerId, package, request, ct);

            // DB20 — gói PHẢI sinh được credit thì mới bán được qua đường CreditPack.
            // Trước đây không guard: mua gói Subscription (InterviewCredits null — hợp lệ theo
            // PackageService.Validate, vốn chỉ bắt buộc credits cho OneTime) hoặc OneTime credits=0
            // vẫn tạo được đơn Kind=CreditPack. Tới lúc webhook Paid thì `credits ?? 0` = 0 → ledger
            // Delta=0 vi phạm CHECK ck_credit_transactions_delta_nonzero → SaveChanges ném →
            // tx.Commit KHÔNG chạy → flip Pending→Paid ROLLBACK theo ⇒ khách ĐÃ TRẢ TIỀN mà đơn kẹt
            // Pending vĩnh viễn, và vì lỗi deterministic nên mọi đường cứu (OrderStatusService polling,
            // OrderExpiryReconciler) đều re-fail. Chặn ở đây = fail 400 SỚM, trước khi tiền rời tay.
            // F8 — nhánh Subscription đã rẽ ở trên nên vế này giờ chỉ còn là LƯỚI AN TOÀN cho giá trị
            // PackageType được thêm về sau: loại gói mới nào chưa có đường bán riêng thì fail 400 sớm,
            // chứ không âm thầm trôi vào đường CreditPack rồi nổ ở webhook như DB20 đã dạy.
            if (package.Type != PackageType.OneTime)
                throw new InvalidOperationException(
                    $"Package type '{package.Type}' cannot be purchased as a credit pack.");

            if (package.InterviewCredits is not > 0)
                throw new InvalidOperationException(
                    "Package does not grant any interview credits and cannot be purchased.");

            // BF3 — guard PayOS config SỚM (trước khi persist) → thiếu ReturnUrl/CancelUrl thì fail
            // 502 sạch, KHÔNG tạo order mồ côi (bug bắt ở layer-3: PayOS reject "return_url null").
            // Redirect theo khu vực FE người mua: dùng URL request (candidate/employer) nếu hợp lệ, else config.
            var (returnUrl, cancelUrl) = PayosUrlResolver.Resolve(request.ReturnUrl, request.CancelUrl, _settings.Value);

            // 2. Generate a unique positive long order code for PayOS (P7 — time+random, ≤2^53−1, UNIQUE+retry).
            var orderCode = await _orderCodes.GenerateAsync(ct);

            // 3. Persist order first (pending)
            var order = new Order
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                Kind = OrderKind.CreditPack,
                PackageId = package.Id,
                AmountVnd = package.PriceVnd,
                PayosOrderCode = orderCode,
                ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            };

            _db.Orders.Add(order);
            await _db.SaveChangesAsync(ct);

            // 4. Create PayOS payment link
            var paymentData = new CreatePaymentLinkRequest
            {
                OrderCode = orderCode,
                Amount = package.PriceVnd,
                Description = $"DH{order.Id:N}"[..25],  // PayOS max 25 chars
                ReturnUrl = returnUrl,
                CancelUrl = cancelUrl,
                ExpiredAt = new DateTimeOffset(order.ExpiredAt).ToUnixTimeSeconds(),
                Items =
            [
                new PaymentLinkItem
                {
                    Name     = package.Name,
                    Quantity = 1,
                    Price    = package.PriceVnd,
                }
            ],
            };

            var response = OrderResponse.ToResponse(order);
            response.CheckoutUrl = await CreatePayosLinkAsync(paymentData);
            return response;
        }

        /// <summary>
        /// F8 — tạo đơn mua/gia hạn thuê bao. Giống đường CreditPack ở phần cơ khí (order_code P7 + link
        /// PayOS), khác ở chỗ <c>Kind</c> KHÔNG phải <see cref="OrderKind.CreditPack"/> nên webhook sẽ rẽ
        /// sang nhánh kích hoạt kỳ hạn — không cộng credit, không ghi sổ cái, không có cửa nào chạm CHECK
        /// <c>delta &lt;&gt; 0</c> (chính là đường mà DB20 phải bịt).
        ///
        /// <c>SubscriptionPurchase</c> vs <c>SubscriptionRenewal</c> chỉ mang nghĩa BÁO CÁO (mua mới hay
        /// gia hạn khi còn hạn), suy lúc tạo đơn. Đường kích hoạt xử lý hai kind y hệt nhau, nên đơn nằm
        /// chờ lâu tới mức thuê bao hết hạn trước khi trả tiền cũng không sinh hành vi lệch.
        /// </summary>
        private async Task<OrderResponse> CreateSubscriptionOrderAsync(
            OwnerType ownerType, Guid ownerId, ProductPackage package, CreateOrderRequest request, CancellationToken ct)
        {
            // Gói thuê bao không có duration_days thì không bán được: không biết bán bao nhiêu ngày.
            // Chặn ở đây = 400 TRƯỚC khi tiền rời tay (cùng tinh thần DB20), thay vì để tiền vào rồi
            // không kích hoạt được kỳ hạn.
            if (package.DurationDays is not > 0)
                throw new InvalidOperationException(
                    "Subscription package has no duration and cannot be purchased.");

            var now = DateTime.UtcNow;
            var renewing = await _db.Subscriptions
                .AnyAsync(s => s.OwnerType == ownerType && s.OwnerId == ownerId
                               && s.Status == SubscriptionStatus.Active
                               && s.ExpiresAt > now, ct);

            var (returnUrl, cancelUrl) = PayosUrlResolver.Resolve(request.ReturnUrl, request.CancelUrl, _settings.Value);
            var orderCode = await _orderCodes.GenerateAsync(ct);

            var order = new Order
            {
                // Id/CreatedAt sinh phía C# thay vì dựa vào DEFAULT `gen_random_uuid()`/`now()`: cả hai là
                // hàm CHỈ có ở Postgres, nên đường ghi này không chạy nổi dưới SQLite (EnsureCreated) ⇒
                // không test được. Mọi entity khác trong service đã tự sinh ở C# (reservation/transaction/
                // invoice…, và campaign C11 cùng lý do); giá trị tường minh được EF ưu tiên nên hành vi
                // trên Postgres không đổi.
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                OwnerType = ownerType,
                OwnerId = ownerId,
                Kind = renewing ? OrderKind.SubscriptionRenewal : OrderKind.SubscriptionPurchase,
                PackageId = package.Id,
                AmountVnd = package.PriceVnd,
                PayosOrderCode = orderCode,
                ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            };

            _db.Orders.Add(order);
            await _db.SaveChangesAsync(ct);

            var paymentData = new CreatePaymentLinkRequest
            {
                OrderCode = orderCode,
                Amount = package.PriceVnd,
                Description = $"DH{order.Id:N}"[..25],  // PayOS max 25 chars (PAY-9)
                ReturnUrl = returnUrl,
                CancelUrl = cancelUrl,
                ExpiredAt = new DateTimeOffset(order.ExpiredAt).ToUnixTimeSeconds(),
                Items =
            [
                new PaymentLinkItem
                {
                    Name     = package.Name,
                    Quantity = 1,
                    Price    = package.PriceVnd,
                }
            ],
            };

            var response = OrderResponse.ToResponse(order);
            response.CheckoutUrl = await CreatePayosLinkAsync(paymentData);
            return response;
        }

        // P8b — tạo đơn tất toán hóa đơn postpaid. Cùng đường CreateOrder (order_code P7 + link PayOS),
        // chỉ khác: kind=InvoiceSettlement, KHÔNG có package (invoice_id thay thế), amount = invoice.Amount,
        // owner lấy từ hóa đơn (nguồn chân lý). Webhook Paid → WebhookService branch theo Kind: settle hóa đơn
        // Issued→Paid (KHÔNG cộng credit).
        public async Task<OrderResponse> CreateInvoiceSettlementOrderAsync(Invoice invoice, CancellationToken ct = default)
        {
            // BF3 — guard PayOS config sớm (như CreateOrderAsync): thiếu URL → 502, không order mồ côi.
            EnsurePayosUrlsConfigured(_settings.Value);

            var orderCode = await _orderCodes.GenerateAsync(ct);

            // amount_vnd là int trong schema orders (tiền lượt VND nguyên) — quy đổi từ invoice.Amount (numeric).
            var amountVnd = (int)decimal.Round(invoice.Amount, MidpointRounding.AwayFromZero);

            var order = new Order
            {
                OwnerType = invoice.OwnerType,
                OwnerId = invoice.OwnerId,
                Kind = OrderKind.InvoiceSettlement,
                InvoiceId = invoice.Id,
                AmountVnd = amountVnd,
                PayosOrderCode = orderCode,
                ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            };

            _db.Orders.Add(order);
            await _db.SaveChangesAsync(ct);

            var cfg = _settings.Value;

            var paymentData = new CreatePaymentLinkRequest
            {
                OrderCode = orderCode,
                Amount = amountVnd,
                Description = $"DH{order.Id:N}"[..25],  // PayOS max 25 chars
                ReturnUrl = cfg.ReturnUrl,
                CancelUrl = cfg.CancelUrl,
                ExpiredAt = new DateTimeOffset(order.ExpiredAt).ToUnixTimeSeconds(),
                Items =
            [
                new PaymentLinkItem
                {
                    Name     = "Invoice settlement",
                    Quantity = 1,
                    Price    = amountVnd,
                }
            ],
            };

            var response = OrderResponse.ToResponse(order);
            response.CheckoutUrl = await CreatePayosLinkAsync(paymentData);
            return response;
        }

        // BF3 — cấu hình PayOS bắt buộc: PayOS reject payment-link nếu return_url/cancel_url null.
        // Invoice settlement không có URL request → chỉ dùng config (fallback). Thiếu → 502 sạch.
        private static void EnsurePayosUrlsConfigured(PayOSSettings cfg) =>
            PayosUrlResolver.Resolve(null, null, cfg);

        // BF3 — bọc call PayOS: ApiException (PayOS từ chối/upstream lỗi) → PaymentGatewayException
        // → controller map 502, không để SDK exception văng thành 500 stack thô.
        private async Task<string> CreatePayosLinkAsync(CreatePaymentLinkRequest paymentData)
        {
            try
            {
                var result = await _payos.PaymentRequests.CreateAsync(paymentData);
                return result.CheckoutUrl;
            }
            catch (PayOS.Exceptions.ApiException ex)
            {
                throw new PaymentGatewayException($"PayOS từ chối tạo payment-link: {ex.Message}", ex);
            }
        }

        public async Task<OrderResponse?> GetOrderAsync(Guid id, CancellationToken ct = default)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
            return order is null ? null : OrderResponse.ToResponse(order);
        }

        // Trang đơn hàng của CHÍNH chủ ví (PAY-2/D15 — owner lấy từ JWT, KHÔNG nhận từ query).
        // Keyset-paged (DB8): mới nhất trước theo (CreatedAt DESC, Id DESC); cursor rỗng/rác = trang đầu;
        // limit mặc định 500 (= hành vi trước phân trang). Optional lọc status — đẩy xuống SQL, KHÔNG
        // lọc sau khi cắt trang (lọc client-side sau Take sẽ trả trang thiếu/rỗng sai).
        //
        // Vì sao set này phình: mỗi lần bấm checkout là INSERT 1 `orders` (ý định trả tiền, không phải
        // trả tiền xong) → đơn Pending bỏ dở tích lại vĩnh viễn, không job nào dọn. Không phân trang thì
        // màn billing tương tác này ngày càng nặng theo số lần user bấm mua.
        //
        // ⚠ Điều kiện owner là VÔ ĐIỀU KIỆN và đứng TRƯỚC mọi filter/cursor: cursor chỉ mang
        // (created_at, id) — KHÔNG mang owner — nên nếu bỏ vị ngữ owner thì cursor sẽ dẫn thẳng sang
        // đơn của chủ ví khác. Index ix_orders_owner_created (owner_type, owner_id, created_at DESC,
        // id DESC) khớp đúng hình dạng này → không cần index mới.
        public async Task<KeysetPage<OrderResponse>> GetOwnerOrdersAsync(
            OwnerType ownerType, Guid ownerId, OrderStatus? status, string? cursor, int? limit, CancellationToken ct = default)
        {
            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);

            var query = _db.Orders.Where(o => o.OwnerType == ownerType && o.OwnerId == ownerId);

            if (status is OrderStatus s)
                query = query.Where(o => o.Status == s);
            if (cur is not null)
                query = query.Where(o => o.CreatedAt < cur.CreatedAt
                    || (o.CreatedAt == cur.CreatedAt && o.Id.CompareTo(cur.Id) < 0));

            var rows = await query
                .OrderByDescending(o => o.CreatedAt)
                .ThenByDescending(o => o.Id)
                .Take(take)
                .ToListAsync(ct);

            var items = rows.Select(OrderResponse.ToResponse).ToList();
            var next = rows.Count == take
                ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
                : null;
            return new KeysetPage<OrderResponse>(items, next);
        }

        // AUTH-7: PlatformAdmin oversight — MỌI đơn xuyên chủ ví (KHÔNG lọc owner, khác GetOwnerOrdersAsync).
        // Optional lọc status (numeric OrderStatus) + ownerType. Keyset-paged (DB8): mới nhất trước
        // theo (CreatedAt DESC, Id DESC); cursor rỗng = trang đầu; limit mặc định 500 (giữ hành vi cũ).
        public async Task<KeysetPage<OrderResponse>> ListAllOrdersAsync(
            OrderStatus? status, OwnerType? ownerType, string? cursor, int? limit, CancellationToken ct = default)
        {
            var take = KeysetPaging.ClampLimit(limit);
            var cur = KeysetCursor.Decode(cursor);

            var query = _db.Orders.AsQueryable();

            if (status is OrderStatus s)
                query = query.Where(o => o.Status == s);
            if (ownerType is OwnerType ot)
                query = query.Where(o => o.OwnerType == ot);
            if (cur is not null)
                query = query.Where(o => o.CreatedAt < cur.CreatedAt
                    || (o.CreatedAt == cur.CreatedAt && o.Id.CompareTo(cur.Id) < 0));

            var rows = await query
                .OrderByDescending(o => o.CreatedAt)
                .ThenByDescending(o => o.Id)
                .Take(take)
                .ToListAsync(ct);

            var items = rows.Select(OrderResponse.ToResponse).ToList();
            var next = rows.Count == take
                ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
                : null;
            return new KeysetPage<OrderResponse>(items, next);
        }

        public async Task CancelOrderAsync(Guid id, CancellationToken ct = default)
        {
            var order = await _db.Orders.FindAsync(id, ct)
                ?? throw new KeyNotFoundException("Order not found.");

            if (order.Status != OrderStatus.Pending)
                throw new InvalidOperationException($"Cannot cancel an order with status '{order.Status}'.");

            await _payos.PaymentRequests.CancelAsync(order.PayosOrderCode, "Cancelled by user");

            // PAY-10: user chủ động huỷ → Cancelled (KHÔNG phải Failed = thanh toán hỏng). Giữ đủ 4
            // trạng thái terminal để đối soát phân biệt được "user tự huỷ" với "cổng thanh toán lỗi".
            order.Status = OrderStatus.Cancelled;
            await _db.SaveChangesAsync(ct);
        }
    }
}
