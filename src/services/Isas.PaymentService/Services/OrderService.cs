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

        public async Task<List<OrderResponse>> GetOwnerOrdersAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            return await _db.Orders
                .Where(o => o.OwnerType == ownerType && o.OwnerId == ownerId)
                .OrderByDescending(o => o.CreatedAt)
                .Select(o => OrderResponse.ToResponse(o))
                .ToListAsync(ct);
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

            order.Status = OrderStatus.Failed;
            await _db.SaveChangesAsync(ct);
        }
    }
}
