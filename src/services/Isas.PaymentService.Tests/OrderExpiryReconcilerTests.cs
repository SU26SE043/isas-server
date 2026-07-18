using System.Reflection;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// PAY-10 — OrderExpiryReconciler đóng đơn Pending quá hạn sang Expired.
/// Bug gốc (e2e 2026-07-18): KHÔNG chỗ nào gán OrderStatus.Expired → 16/16 đơn Pending quá hạn tồn mãi.
///
/// Trọng tâm test = các nhánh AN TOÀN, vì đóng mù là mất tiền thật (user trả phút chót, webhook về trễ,
/// đơn đã Expired ⇒ PAY-10 chặn cộng credit): PayOS Paid → cộng credit KHÔNG đóng · Underpaid → giữ ·
/// PayOS lỗi → giữ · chưa hết ân hạn → giữ · webhook set Paid song song → KHÔNG ghi đè.
/// </summary>
public class OrderExpiryReconcilerTests
{
    private static async Task ScanOnce(OrderExpiryReconciler r)
    {
        var mi = typeof(OrderExpiryReconciler)
            .GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)mi.Invoke(r, new object[] { CancellationToken.None })!;
    }

    private static (OrderExpiryReconciler r, ServiceProvider provider) Build(
        PaymentTestDb tdb,
        IPayOsQueryClient payos,
        IWebhookService webhooks,
        bool enabled = true,
        int graceMinutes = 10,
        int forceExpireAfterDays = 7)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentDbContext>(o => o
            .UseSqlite(tdb.Connection)
            .UseSnakeCaseNamingConvention());
        services.AddSingleton(payos);
        services.AddSingleton(webhooks);
        var provider = services.BuildServiceProvider();

        var r = new OrderExpiryReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OrderExpirySettings
            {
                Enabled = enabled,
                ScanIntervalSeconds = 300,
                GracePeriodMinutes = graceMinutes,
                ForceExpireAfterDays = forceExpireAfterDays,
            }),
            NullLogger<OrderExpiryReconciler>.Instance);
        return (r, provider);
    }

    private static Order SeedOrder(PaymentTestDb tdb, OrderStatus status, DateTime expiredAt, long orderCode)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = Guid.NewGuid(),
            Kind = OrderKind.CreditPack,
            Status = status,
            AmountVnd = 2000,
            PayosOrderCode = orderCode,
            ExpiredAt = expiredAt,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
        };
        tdb.Db.Orders.Add(order);
        tdb.Db.SaveChanges();
        return order;
    }

    private static IPayOsQueryClient PayosReturning(PayOsPaymentStatus status)
    {
        var m = new Mock<IPayOsQueryClient>();
        m.Setup(p => p.GetPaymentInfoAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayOsPaymentInfo(status, "txn-1", "{}"));
        return m.Object;
    }

    private static async Task<OrderStatus> StatusOf(PaymentTestDb tdb, Guid id)
    {
        using var fresh = tdb.NewContext();
        var o = await fresh.Orders.AsNoTracking().FirstAsync(x => x.Id == id);
        return o.Status;
    }

    // Đơn quá hạn + PayOS xác nhận link chết → đóng Expired (đường chính, cái bug cũ thiếu hẳn).
    [Theory]
    [InlineData(PayOsPaymentStatus.Expired)]
    [InlineData(PayOsPaymentStatus.Cancelled)]
    [InlineData(PayOsPaymentStatus.Failed)]
    [InlineData(PayOsPaymentStatus.Pending)]
    public async Task Don_qua_han_va_payos_khong_paid_thi_dong_Expired(PayOsPaymentStatus payosStatus)
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddHours(-1), 2607181200000001);
        var (r, provider) = Build(tdb, PayosReturning(payosStatus), Mock.Of<IWebhookService>());
        using (provider)
        {
            await ScanOnce(r);
        }

        Assert.Equal(OrderStatus.Expired, await StatusOf(tdb, order.Id));
    }

    // AN TOÀN #1: PayOS báo Paid (webhook rơi) → cộng credit qua đường chuẩn, KHÔNG đóng Expired.
    // Đây chính là ca "đóng mù = mất tiền" mà thiết kế phải tránh.
    [Fact]
    public async Task Payos_bao_Paid_thi_cong_credit_va_KHONG_dong()
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddHours(-1), 2607181200000002);

        var webhooks = new Mock<IWebhookService>();
        webhooks.Setup(w => w.ApplyPaidWebhookAsync(
                It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebhookApplyOutcome.Credited);

        var (r, provider) = Build(tdb, PayosReturning(PayOsPaymentStatus.Paid), webhooks.Object);
        using (provider)
        {
            await ScanOnce(r);
        }

        // Cộng credit đúng 1 lần qua đường webhook (idempotent PAY-8), và đơn KHÔNG bị đẩy sang Expired.
        webhooks.Verify(w => w.ApplyPaidWebhookAsync(
            order.PayosOrderCode, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.NotEqual(OrderStatus.Expired, await StatusOf(tdb, order.Id));
    }

    // AN TOÀN #2: Underpaid = tiền đã chuyển một phần → giữ Pending cho người đối soát tay.
    [Fact]
    public async Task Underpaid_thi_giu_Pending_de_doi_soat_tay()
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddHours(-1), 2607181200000003);
        var (r, provider) = Build(tdb, PayosReturning(PayOsPaymentStatus.Underpaid), Mock.Of<IWebhookService>());
        using (provider)
        {
            await ScanOnce(r);
        }

        Assert.Equal(OrderStatus.Pending, await StatusOf(tdb, order.Id));
    }

    // AN TOÀN #3: PayOS lỗi → KHÔNG đóng mù ("hỏi không được" ≠ "chưa trả"), giữ Pending thử lại vòng sau.
    [Fact]
    public async Task Payos_loi_thi_giu_Pending_khong_dong_mu()
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddHours(-1), 2607181200000004);

        var payos = new Mock<IPayOsQueryClient>();
        payos.Setup(p => p.GetPaymentInfoAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("PayOS unreachable"));

        var (r, provider) = Build(tdb, payos.Object, Mock.Of<IWebhookService>());
        using (provider)
        {
            await ScanOnce(r);   // không được ném ra ngoài
        }

        Assert.Equal(OrderStatus.Pending, await StatusOf(tdb, order.Id));
    }

    // Chặn trên chống retry vô hạn: PayOS mãi không xác minh được ("Mã thanh toán không tồn tại" — đơn
    // thời BF3 tạo link thất bại) + đơn đã quá hạn quá ForceExpireAfterDays → vẫn đóng.
    // Quan sát thật 2026-07-18 trên production: 2 đơn từ 13/07 lặp lại mỗi vòng quét.
    [Fact]
    public async Task Payos_loi_ma_don_qua_cu_thi_dong_theo_chan_tren()
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddDays(-8), 2607181200000009);

        var payos = new Mock<IPayOsQueryClient>();
        payos.Setup(p => p.GetPaymentInfoAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Mã thanh toán không tồn tại"));

        var (r, provider) = Build(tdb, payos.Object, Mock.Of<IWebhookService>(), forceExpireAfterDays: 7);
        using (provider)
        {
            await ScanOnce(r);
        }

        Assert.Equal(OrderStatus.Expired, await StatusOf(tdb, order.Id));
    }

    // Chặn trên KHÔNG được nuốt đơn mới quá hạn: PayOS lỗi tạm thời + đơn còn trẻ → vẫn giữ Pending.
    [Fact]
    public async Task Payos_loi_nhung_don_con_moi_thi_van_giu_Pending()
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddHours(-1), 2607181200000010);

        var payos = new Mock<IPayOsQueryClient>();
        payos.Setup(p => p.GetPaymentInfoAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("PayOS 503"));

        var (r, provider) = Build(tdb, payos.Object, Mock.Of<IWebhookService>(), forceExpireAfterDays: 7);
        using (provider)
        {
            await ScanOnce(r);
        }

        Assert.Equal(OrderStatus.Pending, await StatusOf(tdb, order.Id));
    }

    // ForceExpireAfterDays = 0 → tắt chặn trên, giữ Pending mãi kể cả đơn rất cũ (thoát hiểm bằng config).
    [Fact]
    public async Task Chan_tren_tat_thi_don_cu_van_giu_Pending()
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddDays(-30), 2607181200000011);

        var payos = new Mock<IPayOsQueryClient>();
        payos.Setup(p => p.GetPaymentInfoAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Mã thanh toán không tồn tại"));

        var (r, provider) = Build(tdb, payos.Object, Mock.Of<IWebhookService>(), forceExpireAfterDays: 0);
        using (provider)
        {
            await ScanOnce(r);
        }

        Assert.Equal(OrderStatus.Pending, await StatusOf(tdb, order.Id));
    }

    // AN TOÀN #4: chưa hết ân hạn → chưa đụng tới (webhook Paid vẫn còn cửa về).
    [Fact]
    public async Task Chua_het_an_han_thi_khong_dong()
    {
        using var tdb = new PaymentTestDb();
        // Quá expired_at 2 phút nhưng ân hạn 10 phút → chưa tới lượt.
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddMinutes(-2), 2607181200000005);
        var payos = new Mock<IPayOsQueryClient>();
        var (r, provider) = Build(tdb, payos.Object, Mock.Of<IWebhookService>(), graceMinutes: 10);
        using (provider)
        {
            await ScanOnce(r);
        }

        Assert.Equal(OrderStatus.Pending, await StatusOf(tdb, order.Id));
        // Chưa tới lượt thì thậm chí không tốn 1 call PayOS nào.
        payos.Verify(p => p.GetPaymentInfoAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // AN TOÀN #5 (race): webhook set Paid CHEN GIỮA lúc quét (sau khi chọn ứng viên, trước khi flip)
    // → guard WHERE status=Pending khớp 0 row → sweeper KHÔNG ghi đè Paid thành Expired.
    // Mô phỏng race bằng side-effect: đúng lúc hỏi PayOS thì đơn được webhook khác set Paid.
    [Fact]
    public async Task Webhook_set_Paid_chen_giua_thi_khong_bi_ghi_de_thanh_Expired()
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddHours(-1), 2607181200000008);

        var payos = new Mock<IPayOsQueryClient>();
        payos.Setup(p => p.GetPaymentInfoAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // Webhook thật về ngay lúc này và đóng đơn Pending→Paid.
                using var other = tdb.NewContext();
                other.Orders.Where(o => o.Id == order.Id)
                    .ExecuteUpdate(s => s
                        .SetProperty(o => o.Status, OrderStatus.Paid)
                        .SetProperty(o => o.PaidAt, _ => DateTime.UtcNow));
                // PayOS (đọc trễ) vẫn báo Expired → nếu không có guard, sweeper sẽ đè Paid.
                return new PayOsPaymentInfo(PayOsPaymentStatus.Expired, "txn-race", "{}");
            });

        var (r, provider) = Build(tdb, payos.Object, Mock.Of<IWebhookService>());
        using (provider)
        {
            await ScanOnce(r);
        }

        Assert.Equal(OrderStatus.Paid, await StatusOf(tdb, order.Id));
    }

    // Đơn đã terminal (Paid/Cancelled/Failed/Expired) không bao giờ là ứng viên — PAY-10 bất biến.
    [Theory]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Expired)]
    public async Task Don_terminal_khong_bi_dung_toi(OrderStatus terminal)
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, terminal, DateTime.UtcNow.AddHours(-1), 2607181200000006);
        var payos = new Mock<IPayOsQueryClient>();
        var (r, provider) = Build(tdb, payos.Object, Mock.Of<IWebhookService>());
        using (provider)
        {
            await ScanOnce(r);
        }

        Assert.Equal(terminal, await StatusOf(tdb, order.Id));
        payos.Verify(p => p.GetPaymentInfoAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Enabled=false → tắt an toàn, không quét, không gọi PayOS.
    [Fact]
    public async Task Disabled_thi_khong_lam_gi()
    {
        using var tdb = new PaymentTestDb();
        var order = SeedOrder(tdb, OrderStatus.Pending, DateTime.UtcNow.AddHours(-1), 2607181200000007);
        var payos = new Mock<IPayOsQueryClient>();
        var (r, provider) = Build(tdb, payos.Object, Mock.Of<IWebhookService>(), enabled: false);
        using (provider)
        {
            await ScanOnce(r);
        }

        Assert.Equal(OrderStatus.Pending, await StatusOf(tdb, order.Id));
        payos.Verify(p => p.GetPaymentInfoAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
