using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Webhook PayOS ĐỐI CHIẾU SỐ TIỀN — `data.amount` là tiền của MỘT giao dịch chuyển vào, không phải của
/// link (PayOS cho trả nhiều lần: amountPaid/amountRemaining, trạng thái UNDERPAID). Trước bản này
/// `success=true` là lật Paid + cộng ĐỦ credit bất kể chuyển bao nhiêu.
/// Luật: thiếu tiền → hỏi lại PayOS; link Paid ⇒ áp (trả nhiều lần gộp đủ); chưa Paid / không hỏi được
/// ⇒ GIỮ Pending + bằng chứng `underpaid` (fail-closed). Overload không số tiền (poll/sweeper) đi thẳng.
/// </summary>
public class WebhookAmountCheckTests
{
    private sealed class StubPayOs : IPayOsQueryClient
    {
        private readonly PayOsPaymentStatus? _status;
        public int CallCount { get; private set; }
        public StubPayOs(PayOsPaymentStatus? status) => _status = status;

        public Task<PayOsPaymentInfo> GetPaymentInfoAsync(long orderCode, CancellationToken ct = default)
        {
            CallCount++;
            if (_status is null) throw new InvalidOperationException("PayOS down");
            return Task.FromResult(new PayOsPaymentInfo(_status.Value, "FTLINK", "{\"link\":1}"));
        }
    }

    private static WebhookService NewService(PaymentTestDb tdb, IPayOsQueryClient? payos = null)
    {
        var ctx = tdb.NewContext();
        return new WebhookService(ctx, new CreditAccountService(ctx), payos: payos);
    }

    private static async Task<(Order Order, Guid OwnerId)> SeedAsync(
        PaymentTestDb tdb, long orderCode, long amountVnd = 100_000, OrderStatus status = OrderStatus.Pending)
    {
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(), Name = "Pack 10", Type = PackageType.OneTime,
            PriceVnd = amountVnd, InterviewCredits = 10, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        var ownerId = Guid.NewGuid();
        var order = new Order
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = ownerId, Kind = OrderKind.CreditPack,
            PackageId = pkg.Id, Status = status, AmountVnd = amountVnd, PayosOrderCode = orderCode,
            PaidAt = status == OrderStatus.Paid ? DateTime.UtcNow : null,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30), CreatedAt = DateTime.UtcNow
        };
        tdb.Db.ProductPackages.Add(pkg);
        tdb.Db.Orders.Add(order);
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active,
            RemainingCredits = 3, ReservedCredits = 0, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
        return (order, ownerId);
    }

    private static async Task AssertPendingKhongCongAsync(PaymentTestDb tdb, Order order, Guid ownerId)
    {
        using var read = tdb.NewContext();
        var o = await read.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal(OrderStatus.Pending, o.Status);
        Assert.Null(o.PaidAt);
        Assert.Equal(3, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits);
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.OrderId == order.Id));
    }

    // Thiếu tiền + KHÔNG có client PayOS (không hỏi lại được) → fail-closed: Pending, 0 credit, bằng chứng underpaid.
    [Fact]
    public async Task ThieuTien_KhongHoiDuocPayOs_GiuPending_KhongCongCredit_GhiBangChungUnderpaid()
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedAsync(tdb, 260915000001, amountVnd: 100_000);

        var outcome = await NewService(tdb, payos: null)
            .ApplyPaidWebhookAsync(order.PayosOrderCode, amountPaidVnd: 60_000, "FT1", "{\"amount\":60000}");

        Assert.Equal(WebhookApplyOutcome.Underpaid, outcome);
        await AssertPendingKhongCongAsync(tdb, order, ownerId);

        using var read = tdb.NewContext();
        var evidence = await read.PaymentTransactions.Where(t => t.OrderId == order.Id).ToListAsync();
        Assert.Single(evidence);
        Assert.Equal("underpaid", evidence[0].Status);
        Assert.Equal("{\"amount\":60000}", evidence[0].RawWebhookPayload);
    }

    // Thiếu tiền + PayOS bảo link CHƯA Paid (Underpaid) → giữ Pending.
    [Fact]
    public async Task ThieuTien_PayOsBaoUnderpaid_GiuPending()
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedAsync(tdb, 260915000002);
        var payos = new StubPayOs(PayOsPaymentStatus.Underpaid);

        var outcome = await NewService(tdb, payos)
            .ApplyPaidWebhookAsync(order.PayosOrderCode, amountPaidVnd: 99_999, "FT1", "{}");

        Assert.Equal(WebhookApplyOutcome.Underpaid, outcome);
        Assert.Equal(1, payos.CallCount);
        await AssertPendingKhongCongAsync(tdb, order, ownerId);
    }

    // Thiếu tiền + PayOS LỖI khi hỏi → "không hỏi được" ≠ "đã đủ" → giữ Pending.
    [Fact]
    public async Task ThieuTien_PayOsLoi_GiuPending()
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedAsync(tdb, 260915000003);
        var payos = new StubPayOs(status: null);

        var outcome = await NewService(tdb, payos)
            .ApplyPaidWebhookAsync(order.PayosOrderCode, amountPaidVnd: 1, "FT1", "{}");

        Assert.Equal(WebhookApplyOutcome.Underpaid, outcome);
        await AssertPendingKhongCongAsync(tdb, order, ownerId);
    }

    // Thiếu tiền NHƯNG PayOS xác nhận link đã Paid (khách chuyển 2 lần gộp đủ) → áp bình thường.
    [Fact]
    public async Task ThieuTien_PayOsXacNhanLinkPaid_CongCredit()
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedAsync(tdb, 260915000004);
        var payos = new StubPayOs(PayOsPaymentStatus.Paid);

        var outcome = await NewService(tdb, payos)
            .ApplyPaidWebhookAsync(order.PayosOrderCode, amountPaidVnd: 40_000, "FT2", "{}");

        Assert.Equal(WebhookApplyOutcome.Credited, outcome);
        Assert.Equal(1, payos.CallCount);
        using var read = tdb.NewContext();
        Assert.Equal(OrderStatus.Paid, (await read.Orders.SingleAsync(x => x.Id == order.Id)).Status);
        Assert.Equal(13, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits);
        Assert.Equal(1, await read.CreditTransactions.CountAsync(t => t.OrderId == order.Id));
    }

    // Đủ tiền / thừa tiền → KHÔNG hỏi PayOS, áp ngay.
    [Theory]
    [InlineData(100_000)]
    [InlineData(150_000)]
    public async Task DuTien_KhongHoiPayOs_CongCredit(long amountPaid)
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedAsync(tdb, 260915000005 + amountPaid);
        var payos = new StubPayOs(PayOsPaymentStatus.Pending);   // nếu bị hỏi sẽ ra Pending → lộ ngay

        var outcome = await NewService(tdb, payos)
            .ApplyPaidWebhookAsync(order.PayosOrderCode, amountPaidVnd: amountPaid, "FT", "{}");

        Assert.Equal(WebhookApplyOutcome.Credited, outcome);
        Assert.Equal(0, payos.CallCount);
        using var read = tdb.NewContext();
        Assert.Equal(13, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits);
    }

    // Overload KHÔNG có số tiền (poll P3 / sweeper — PayOS đã báo Paid cho cả link) → đi thẳng như trước.
    [Fact]
    public async Task KhongCoSoTien_DuongPollSweeper_ApNhuCu()
    {
        using var tdb = new PaymentTestDb();
        var (order, ownerId) = await SeedAsync(tdb, 260915000006);

        var outcome = await NewService(tdb, new StubPayOs(PayOsPaymentStatus.Underpaid))
            .ApplyPaidWebhookAsync(order.PayosOrderCode, "FT", "{}");

        Assert.Equal(WebhookApplyOutcome.Credited, outcome);
        using var read = tdb.NewContext();
        Assert.Equal(13, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits);
    }

    // Đơn ĐÃ terminal + webhook thiếu tiền tới muộn → AlreadyProcessed, KHÔNG ghi thêm bằng chứng vào đơn đã chốt.
    [Fact]
    public async Task DonDaPaid_WebhookThieuTienToiMuon_NoOp_KhongGhiBangChung()
    {
        using var tdb = new PaymentTestDb();
        var (order, _) = await SeedAsync(tdb, 260915000007, status: OrderStatus.Paid);
        var payos = new StubPayOs(PayOsPaymentStatus.Paid);

        var outcome = await NewService(tdb, payos)
            .ApplyPaidWebhookAsync(order.PayosOrderCode, amountPaidVnd: 1, "FT", "{}");

        Assert.Equal(WebhookApplyOutcome.AlreadyProcessed, outcome);
        Assert.Equal(0, payos.CallCount);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.PaymentTransactions.CountAsync(t => t.OrderId == order.Id));
    }

    // HỢP ĐỒNG: WebhookController phải gọi overload CÓ số tiền với `data.Amount` — thiếu vế này thì toàn
    // bộ đối chiếu ở trên là code không ai chạy tới (PayOSClient.Webhooks.VerifyAsync không mock được
    // nên khoá bằng đọc mã nguồn).
    [Fact]
    public void WebhookController_TruyenDataAmountVaoApplyPaid()
    {
        var path = Path.Combine(RepoRoot(), "src", "services", "Isas.PaymentService", "Controllers", "WebhookController.cs");
        var source = File.ReadAllText(path);
        Assert.Contains("ApplyPaidWebhookAsync(data.OrderCode, data.Amount, data.Reference, raw, ct)", source);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "rules.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root không tìm thấy");
    }
}
