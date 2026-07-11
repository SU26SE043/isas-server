using Isas.PaymentService.Models;
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

        public OrderService(PaymentDbContext db, PayOSClient payos, IOptions<PayOSSettings> settings)
        {
            _db = db;
            _payos = payos;
            _settings = settings;
        }

        public async Task<OrderResponse> CreateOrderAsync(Guid userId, CreateOrderRequest request, CancellationToken ct = default)
        {
            // 1. Fetch package
            var package = await _db.ProductPackages.FirstOrDefaultAsync(p => p.Id == request.PackageId, ct)
                ?? throw new KeyNotFoundException("Package not found.");

            if (!package.IsActive)
                throw new InvalidOperationException("Package is no longer available.");

            var existingPending =
                await _db.Orders
                    .Where(x =>
                        x.UserId == userId &&
                        x.PackageId == request.PackageId &&
                        x.Status == OrderStatus.Pending &&
                        x.ExpiredAt > DateTime.UtcNow)
                    .FirstOrDefaultAsync(ct);

            // 2. Generate a unique positive long order code for PayOS
            var orderCode = GenerateOrderCode();

            // 3. Persist order first (pending)
            var order = new Order
            {
                UserId = userId,
                PackageId = package.Id,
                AmountVnd = package.PriceVnd,
                PayosOrderCode = orderCode,
                ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            };

            _db.Orders.Add(order);
            await _db.SaveChangesAsync(ct);

            // 4. Create PayOS payment link
            var cfg = _settings.Value;

            var paymentData = new CreatePaymentLinkRequest
            {
                OrderCode = orderCode,
                Amount = package.PriceVnd,
                Description = $"DH{order.Id:N}"[..25],  // PayOS max 25 chars
                ReturnUrl = cfg.ReturnUrl,
                CancelUrl = cfg.CancelUrl,
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

            var paymentResult = await _payos.PaymentRequests.CreateAsync(paymentData);

            var response = OrderResponse.ToResponse(order);
            response.CheckoutUrl = paymentResult.CheckoutUrl;
            return response;
        }

        public async Task<OrderResponse?> GetOrderAsync(Guid id, CancellationToken ct = default)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
            return order is null ? null : OrderResponse.ToResponse(order);
        }

        public async Task<List<OrderResponse>> GetUserOrdersAsync(Guid userId, CancellationToken ct = default)
        {
            return await _db.Orders
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.CreatedAt)
                .Select(o => OrderResponse.ToResponse(o))
                .ToListAsync(ct);
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

        /// Timestamp (ms) + 3-digit random suffix, stays within JS safe-integer range
        private static long GenerateOrderCode()
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000_000L;
            var suffix = Random.Shared.Next(100, 999);
            return long.Parse($"{ts}{suffix}");
        }
    }
}
