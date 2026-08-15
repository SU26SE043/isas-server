using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Mở rộng F19 (<c>GET /payment/admin/revenue</c>) với 4 nhóm chỉ số Finance cần mà "kế toán tiền mặt
/// trần" (gross/refund/net) không trả lời được: giá vốn AI + biên lợi nhuận gộp, tỷ lệ hoàn %, ARPU, và
/// phễu chuyển đổi đơn hàng. KHÔNG endpoint mới, KHÔNG migration — chỉ mở rộng
/// <see cref="RevenueService.GetRevenueAsync"/> đã có.
///
/// Dùng CHUNG helper <c>new RevenueService(tdb.Db)</c> với <c>RevenueAndLedgerF19Tests</c> (constructor
/// một-tham-số giữ nguyên, tự dựng <c>AiUsageService</c> THẬT — không mock — đọc trên chính DB test) thay
/// vì chỉ vì các test này cần thêm chỉ số mà đè lên hợp đồng cũ.
/// </summary>
public class RevenueMarginAndFunnelTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── seed ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<Order> SeedOrderAsync(
        PaymentTestDb tdb, OrderStatus status, long amount, DateTime createdAt,
        DateTime? paidAt = null, DateTime? refundedAt = null,
        OrderKind kind = OrderKind.CreditPack, Guid? ownerId = null)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId ?? Guid.NewGuid(),
            Kind = kind,
            Status = status,
            AmountVnd = amount,
            PayosOrderCode = Random.Shared.NextInt64(1, long.MaxValue / 4),
            ExpiredAt = createdAt.AddDays(30),
            PaidAt = paidAt,
            RefundedAt = refundedAt,
            CreatedAt = createdAt,
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    /// <summary>Ghi thẳng một dòng chi phí AI — CostUsd đặt trực tiếp (bỏ qua bảng giá) vì
    /// <c>GetReportAsync</c> chỉ cộng cột đã snapshot, không tính lại từ token.</summary>
    private static async Task SeedAiUsageAsync(PaymentTestDb tdb, decimal costUsd, DateTime createdAt)
    {
        tdb.Db.AiUsageLogs.Add(new AiUsageLog
        {
            Id = Guid.NewGuid(),
            Operation = "score",
            Model = "gemini-2.5-flash",
            PromptTokens = 1000,
            OutputTokens = 500,
            TotalTokens = 1500,
            InputPricePerMillionUsd = 0.30m,
            OutputPricePerMillionUsd = 2.50m,
            CostUsd = costUsd,
            CreatedAt = createdAt,
        });
        await tdb.Db.SaveChangesAsync();
    }

    // ── giá vốn AI + biên lợi nhuận gộp ──────────────────────────────────────────────────────

    /// <summary>
    /// Margin PHẢI ra số ÂM khi chi phí AI vượt doanh thu ròng của kỳ, và KHÔNG được kẹp về 0 — số âm là
    /// tín hiệu tài chính thật (kỳ này lỗ vận hành AI), che nó đi là nói dối báo cáo.
    /// </summary>
    [Fact]
    public async Task Margin_ChiPhiAiVuotDoanhThu_RaSoAm_KhongKepVe0()
    {
        using var tdb = new PaymentTestDb();
        await SeedOrderAsync(tdb, OrderStatus.Paid, 10_000, T0.AddDays(1), paidAt: T0.AddDays(1));
        // $5 * 26000 (tỷ giá mặc định) = 130.000đ — vượt xa doanh thu 10.000đ.
        await SeedAiUsageAsync(tdb, 5.0m, T0.AddDays(1));

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(10_000, r.NetRevenueVnd);
        Assert.Equal(5.0m, r.AiCostUsd);
        Assert.Equal(130_000, r.AiCostVnd);
        Assert.Equal(-120_000, r.GrossMarginVnd);      // ÂM — không phải 0
        Assert.True(r.GrossMarginVnd < 0);
    }

    /// <summary>
    /// Margin phải trừ trên <c>NetRevenueVnd</c> (đã trừ hoàn), KHÔNG phải trên <c>GrossRevenueVnd</c>.
    /// Trừ trên gross sẽ báo margin cao hơn thực tế đúng bằng phần đã trả lại khách.
    /// </summary>
    [Fact]
    public async Task Margin_TruTrenNet_KhongPhaiTrenGross()
    {
        using var tdb = new PaymentTestDb();
        await SeedOrderAsync(tdb, OrderStatus.Paid, 500_000, T0.AddDays(1), paidAt: T0.AddDays(1));
        await SeedOrderAsync(tdb, OrderStatus.Refunded, 200_000, T0.AddDays(2),
            paidAt: T0.AddDays(2), refundedAt: T0.AddDays(3));
        // $1 * 26000 = 26.000đ.
        await SeedAiUsageAsync(tdb, 1.0m, T0.AddDays(2));

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(700_000, r.GrossRevenueVnd);
        Assert.Equal(200_000, r.RefundedVnd);
        Assert.Equal(500_000, r.NetRevenueVnd);
        Assert.Equal(26_000, r.AiCostVnd);
        // ĐÚNG: net(500k) − aiCost(26k) = 474k. SAI (nếu trừ trên gross): 700k − 26k = 674k.
        Assert.Equal(474_000, r.GrossMarginVnd);
        Assert.NotEqual(674_000, r.GrossMarginVnd);
    }

    // ── tỷ lệ hoàn ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tỷ lệ hoàn CÓ THỂ VƯỢT 100%: refund đếm theo <c>refunded_at</c>, gross đếm theo <c>paid_at</c> —
    /// một đơn thu ở kỳ TRƯỚC mà hoàn ở kỳ NÀY hoàn toàn hợp lệ và làm tử số > mẫu số của kỳ này. Không
    /// kẹp — số bất thường là điều nên thấy.
    /// </summary>
    [Fact]
    public async Task TyLeHoan_CoTheVuot100PhanTram()
    {
        using var tdb = new PaymentTestDb();
        // Gross nhỏ, thu TRONG kỳ.
        await SeedOrderAsync(tdb, OrderStatus.Paid, 100_000, T0.AddDays(1), paidAt: T0.AddDays(1));
        // Hoàn LỚN của một đơn thu TRƯỚC kỳ (paid_at ngoài [from,to)) nhưng hoàn TRONG kỳ.
        await SeedOrderAsync(tdb, OrderStatus.Refunded, 900_000, T0.AddDays(-10),
            paidAt: T0.AddDays(-10), refundedAt: T0.AddDays(5));

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(100_000, r.GrossRevenueVnd);
        Assert.Equal(900_000, r.RefundedVnd);
        Assert.True(r.RefundRatePct > 100.0);
        Assert.Equal(900.0, r.RefundRatePct, precision: 3);
    }

    // ── ARPU ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>ARPU chia theo số CHỦ VÍ DISTINCT, không phải số đơn — 2 đơn cùng một chủ ví chỉ tính một
    /// người trả tiền.</summary>
    [Fact]
    public async Task Arpu_ChiaTheoChuViDistinct_KhongPhaiTheoSoDon()
    {
        using var tdb = new PaymentTestDb();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        await SeedOrderAsync(tdb, OrderStatus.Paid, 100_000, T0.AddDays(1), paidAt: T0.AddDays(1), ownerId: ownerA);
        await SeedOrderAsync(tdb, OrderStatus.Paid, 200_000, T0.AddDays(2), paidAt: T0.AddDays(2), ownerId: ownerA);
        await SeedOrderAsync(tdb, OrderStatus.Paid, 500_000, T0.AddDays(3), paidAt: T0.AddDays(3), ownerId: ownerB);

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(800_000, r.GrossRevenueVnd);
        Assert.Equal(2, r.PayingOwnerCount);           // 2 chủ ví, KHÔNG phải 3 đơn
        Assert.Equal(400_000, r.ArpuVnd);              // 800k / 2, KHÔNG phải 800k / 3
    }

    // ── phễu chuyển đổi ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phễu đếm MỌI status theo <c>created_at</c>. Đơn Paid/Refunded trong test này có <c>paid_at</c> NẰM
    /// NGOÀI kỳ — chứng minh phễu KHÔNG dùng <c>paid_at</c> (nếu dùng thì hai đơn đó sẽ không xuất hiện ở
    /// đâu cả, vì <c>GrossRevenueVnd</c> cũng loại chúng ra do lọc theo <c>paid_at</c>).
    /// </summary>
    [Fact]
    public async Task Funnel_DemTheoCreatedAt_KhongPhaiTheoPaidAt()
    {
        using var tdb = new PaymentTestDb();
        await SeedOrderAsync(tdb, OrderStatus.Pending, 100_000, T0.AddDays(1));
        await SeedOrderAsync(tdb, OrderStatus.Paid, 200_000, T0.AddDays(2),
            paidAt: T0.AddDays(40));                    // paid_at NGOÀI kỳ [T0, T0+30)
        await SeedOrderAsync(tdb, OrderStatus.Failed, 300_000, T0.AddDays(3));
        await SeedOrderAsync(tdb, OrderStatus.Expired, 400_000, T0.AddDays(4));
        await SeedOrderAsync(tdb, OrderStatus.Cancelled, 500_000, T0.AddDays(5));
        await SeedOrderAsync(tdb, OrderStatus.Refunded, 600_000, T0.AddDays(6),
            paidAt: T0.AddDays(-5), refundedAt: T0.AddDays(50));  // paid_at NGOÀI kỳ, refunded_at cũng ngoài

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        // Cả hai đơn Paid/Refunded có paid_at ngoài kỳ ⇒ GrossRevenueVnd = 0 (trục paid_at, khác trục
        // created_at của phễu) — bằng chứng hai bảng đo hai thứ khác nhau.
        Assert.Equal(0, r.GrossRevenueVnd);

        Assert.Equal(6, r.Funnel.CreatedCount);
        Assert.Equal(1, r.Funnel.PendingCount);
        Assert.Equal(2, r.Funnel.PaidCount);            // Paid + Refunded đều tính "đã từng thu tiền"
        Assert.Equal(1, r.Funnel.FailedCount);
        Assert.Equal(1, r.Funnel.ExpiredCount);
        Assert.Equal(1, r.Funnel.CancelledCount);
        Assert.Equal(2.0 / 6.0 * 100.0, r.Funnel.ConversionRatePct, precision: 6);
    }

    // ── chia cho 0 ───────────────────────────────────────────────────────────────────────────

    /// <summary>Kỳ rỗng hoàn toàn (không đơn nào, không lượt AI nào) — mọi phép chia phải trả 0, không
    /// throw <c>DivideByZeroException</c>.</summary>
    [Fact]
    public async Task KyRong_ChiaCho0_TraSoKhong_KhongThrow()
    {
        using var tdb = new PaymentTestDb();

        var r = await new RevenueService(tdb.Db)
            .GetRevenueAsync(T0, T0.AddDays(30), RevenueGranularity.Day);

        Assert.Equal(0, r.PayingOwnerCount);
        Assert.Equal(0, r.ArpuVnd);
        Assert.Equal(0.0, r.RefundRatePct);
        Assert.Equal(0, r.Funnel.CreatedCount);
        Assert.Equal(0.0, r.Funnel.ConversionRatePct);
        Assert.Equal(0m, r.AiCostUsd);
        Assert.Equal(0, r.AiCostVnd);
        Assert.Equal(0, r.GrossMarginVnd);
    }
}
