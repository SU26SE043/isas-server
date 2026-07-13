using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P7 — order_code = time-based (yyMMddHHmmss, 12 chữ số) + 3 chữ số random = 15 chữ số,
    /// luôn nằm trong <see cref="Ceiling"/> (9.007.199.254.740.991 = 2^53-1, trần PayOS —
    /// docs/decisions.md D12, docs/services/payment.md §PayOS). Không đoán được (random suffix),
    /// không lộ số lượng đơn (không auto-increment), không dùng snowflake 64-bit (vượt trần — D12).
    ///
    /// Đụng UNIQUE(payos_order_code) trên bảng orders → regenerate + retry (bounded), đúng
    /// business rule "order_code" ở payment.md §Business rules / §Validation.
    /// </summary>
    public class OrderCodeGenerator : IOrderCodeGenerator
    {
        /// <summary>Trần PayOS đã verify (D12): số nguyên dương ≤ 2^53-1.</summary>
        public const long Ceiling = 9_007_199_254_740_991L;

        private const int MaxAttempts = 10;

        private readonly PaymentDbContext _db;
        private readonly Func<long> _candidateFactory;

        public OrderCodeGenerator(PaymentDbContext db) : this(db, DefaultCandidate)
        {
        }

        /// <summary>
        /// Ctor cho test: inject 1 candidate factory tuỳ ý để ép va chạm (collision) và kiểm chứng
        /// đường retry mà không phải chờ trùng ngẫu nhiên tự nhiên (xác suất gần như 0).
        /// </summary>
        public OrderCodeGenerator(PaymentDbContext db, Func<long> candidateFactory)
        {
            _db = db;
            _candidateFactory = candidateFactory;
        }

        public async Task<long> GenerateAsync(CancellationToken ct = default)
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var candidate = _candidateFactory();

                // Guard phòng thủ: factory (kể cả factory tuỳ ý ở test) không được sinh ra số
                // ngoài trần/không dương — bỏ qua candidate hỏng, thử lại thay vì trả số sai.
                if (candidate <= 0 || candidate > Ceiling)
                    continue;

                var exists = await _db.Orders.AnyAsync(o => o.PayosOrderCode == candidate, ct);
                if (!exists)
                    return candidate;
            }

            throw new InvalidOperationException(
                $"order_code generator: không tìm được mã chưa dùng sau {MaxAttempts} lần thử.");
        }

        /// <summary>
        /// yyMMddHHmmss (UTC, 12 chữ số) + 4 chữ số random (0000-9999, 10.000 mã/giây) = tối đa
        /// 16 chữ số. Với năm hiện tại (20xx) luôn &lt; <see cref="Ceiling"/> (~9.007×10^15) với
        /// biên an toàn lớn (giữ đúng tới ~năm 2090). Guard trong <see cref="GenerateAsync"/> bỏ
        /// qua + thử lại nếu candidate lỡ vượt trần (fallback tự phục hồi cho ca hiếm/xa tương lai),
        /// nên không cần clamp cứng ở đây.
        /// </summary>
        private static long DefaultCandidate()
        {
            var ts = DateTime.UtcNow.ToString("yyMMddHHmmss");
            var suffix = Random.Shared.Next(0, 10_000).ToString("D4");
            return long.Parse(ts + suffix);
        }
    }
}
