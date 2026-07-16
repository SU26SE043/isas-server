using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// P2 — WebhookService.ApplyPaidWebhookAsync (bỏ qua verify chữ ký — test riêng logic cộng credit).
// Verify:
// (1) webhook Paid lần đầu → order Pending→Paid (+PaidAt), remaining += credits, đúng 1 ledger(Purchase,+N),
//     1 payment_transactions.
// (2) gọi 2 lần cùng orderCode → remaining không cộng lần 2 (idempotent theo order.status terminal, PAY-8/PAY-10).
// (3) chủ ví chưa có account → tạo account rồi cộng (remaining = N).
// (4) orderCode không khớp đơn nào → no-op (OrderNotFound), không throw, có payment_transactions log (order_id null).
public class WebhookServiceTests
{
    private static WebhookService NewService(PaymentTestDb tdb, out PaymentDbContext ctx)
    {
        ctx = tdb.NewContext();
        // WebhookService + CreditAccountService phải DÙNG CHUNG 1 DbContext (cùng transaction lúc tạo ví).
        return new WebhookService(ctx, new CreditAccountService(ctx));
    }

    private static async Task<ProductPackage> SeedPackageAsync(PaymentTestDb tdb, int credits, long priceVnd = 100_000)
    {
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = $"Pack {credits}",
            Type = PackageType.OneTime,
            PriceVnd = priceVnd,
            InterviewCredits = credits,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.ProductPackages.Add(pkg);
        await tdb.Db.SaveChangesAsync();
        return pkg;
    }

    private static async Task<Order> SeedOrderAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, Guid packageId, long orderCode, int amountVnd = 100_000)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            Kind = OrderKind.CreditPack,
            PackageId = packageId,
            Status = OrderStatus.Pending,
            AmountVnd = amountVnd,
            PayosOrderCode = orderCode,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    private static async Task SeedAccountAsync(PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = 0,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
    }

    // (1) Paid lần đầu → order Paid + PaidAt, remaining = r + N, đúng 1 ledger(Purchase,+N), 1 payment_transactions.
    [Fact]
    public async Task ApplyPaid_LanDau_CongCredit_GhiLedger_GhiPaymentTxn()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        const long code = 260711120001;
        var pkg = await SeedPackageAsync(tdb, credits: 10);
        var order = await SeedOrderAsync(tdb, OwnerType.User, ownerId, pkg.Id, code);
        await SeedAccountAsync(tdb, OwnerType.User, ownerId, remaining: 3);

        var svc = NewService(tdb, out _);
        var outcome = await svc.ApplyPaidWebhookAsync(code, gatewayTxnId: "FT123", rawPayload: "{\"raw\":1}");

        Assert.Equal(WebhookApplyOutcome.Credited, outcome);

        using var read = tdb.NewContext();
        var o = await read.Orders.SingleAsync(x => x.PayosOrderCode == code);
        Assert.Equal(OrderStatus.Paid, o.Status);
        Assert.NotNull(o.PaidAt);

        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId);
        Assert.Equal(13, acc.RemainingCredits);   // 3 + 10

        var ledger = await read.CreditTransactions.Where(t => t.OrderId == order.Id).ToListAsync();
        Assert.Single(ledger);
        Assert.Equal(10, ledger[0].Delta);
        Assert.Equal(CreditTransactionReason.Purchase, ledger[0].Reason);
        Assert.Equal(ownerId, ledger[0].OwnerId);
        Assert.Null(ledger[0].SessionId);

        var pt = await read.PaymentTransactions.Where(t => t.OrderId == order.Id).ToListAsync();
        Assert.Single(pt);
        Assert.Equal("payos", pt[0].Gateway);
        Assert.Equal("success", pt[0].Status);
        Assert.Equal("FT123", pt[0].GatewayTxnId);
        Assert.Equal("{\"raw\":1}", pt[0].RawWebhookPayload);
    }

    // (2) gọi 2 lần cùng orderCode → remaining chỉ cộng 1 lần; lần 2 AlreadyProcessed (idempotent PAY-8/PAY-10).
    [Fact]
    public async Task ApplyPaid_GoiLai2Lan_ChiCong1Lan()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        const long code = 260711120002;
        var pkg = await SeedPackageAsync(tdb, credits: 5);
        var order = await SeedOrderAsync(tdb, OwnerType.Org, ownerId, pkg.Id, code);
        await SeedAccountAsync(tdb, OwnerType.Org, ownerId, remaining: 0);

        var first = await NewService(tdb, out _).ApplyPaidWebhookAsync(code, "FT1", "{}");
        var second = await NewService(tdb, out _).ApplyPaidWebhookAsync(code, "FT2", "{}");

        Assert.Equal(WebhookApplyOutcome.Credited, first);
        Assert.Equal(WebhookApplyOutcome.AlreadyProcessed, second);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId);
        Assert.Equal(5, acc.RemainingCredits);   // KHÔNG cộng lần 2 (5, không phải 10)
        Assert.Equal(1, await read.CreditTransactions.CountAsync(t => t.OrderId == order.Id)); // đúng 1 Purchase
        Assert.Equal(1, await read.PaymentTransactions.CountAsync(t => t.OrderId == order.Id)); // lần 2 no-op trước khi ghi
    }

    // (3) chủ ví chưa có account → Apply tạo account rồi cộng (remaining = N).
    [Fact]
    public async Task ApplyPaid_ChuaCoAccount_TaoRoiCong()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        const long code = 260711120003;
        var pkg = await SeedPackageAsync(tdb, credits: 7);
        var order = await SeedOrderAsync(tdb, OwnerType.User, ownerId, pkg.Id, code);
        // KHÔNG seed account

        var outcome = await NewService(tdb, out _).ApplyPaidWebhookAsync(code, "FT3", "{}");

        Assert.Equal(WebhookApplyOutcome.Credited, outcome);
        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerType == OwnerType.User && a.OwnerId == ownerId);
        Assert.Equal(7, acc.RemainingCredits);   // 0 (mới tạo) + 7
        Assert.Equal(PaymentMode.Prepaid, acc.PaymentMode);
        Assert.Single(await read.CreditTransactions.Where(t => t.OrderId == order.Id).ToListAsync());
    }

    // (4) orderCode không khớp đơn nào → OrderNotFound, không throw, có payment_transactions log (order_id null),
    //     KHÔNG cộng credit / không ledger.
    [Fact]
    public async Task ApplyPaid_KhongKhopDon_NoOp_CoLog()
    {
        using var tdb = new PaymentTestDb();
        // seed 1 gói + 1 đơn KHÁC code để chắc chắn không có gì cộng nhầm
        var pkg = await SeedPackageAsync(tdb, credits: 4);
        await SeedOrderAsync(tdb, OwnerType.User, Guid.NewGuid(), pkg.Id, orderCode: 111111111111);

        const long unknown = 999999999999;
        var outcome = await NewService(tdb, out _).ApplyPaidWebhookAsync(unknown, "FTX", "{\"orderCode\":999999999999}");

        Assert.Equal(WebhookApplyOutcome.OrderNotFound, outcome);

        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditTransactions.CountAsync()); // KHÔNG cộng credit
        var logs = await read.PaymentTransactions.ToListAsync();
        Assert.Single(logs);
        Assert.Null(logs[0].OrderId);                 // bằng chứng gắn order_id null
        Assert.Equal("success", logs[0].Status);
        Assert.Equal("{\"orderCode\":999999999999}", logs[0].RawWebhookPayload);
    }

    // (5) P8b — webhook Paid cho đơn InvoiceSettlement → hóa đơn Issued→Paid, KHÔNG cộng credit; idempotent ×2.
    [Fact]
    public async Task ApplyPaid_InvoiceSettlement_SettleHoaDon_KhongCongCredit_Idempotent()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        const long code = 260711120005;

        // Hóa đơn Issued + ví Org (chứng minh KHÔNG cộng credit) + đơn InvoiceSettlement gắn invoice_id.
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PeriodStart = DateTime.UtcNow.AddDays(-30),
            PeriodEnd = DateTime.UtcNow,
            InterviewCount = 6,
            UnitPrice = 50_000,
            Amount = 300_000,
            Status = InvoiceStatus.Issued,
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.Invoices.Add(invoice);
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 4);
        tdb.Db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            Kind = OrderKind.InvoiceSettlement,
            PackageId = null,
            InvoiceId = invoice.Id,
            Status = OrderStatus.Pending,
            AmountVnd = 300_000,
            PayosOrderCode = code,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var first = await NewService(tdb, out _).ApplyPaidWebhookAsync(code, "FT-INV1", "{\"inv\":1}");
        var second = await NewService(tdb, out _).ApplyPaidWebhookAsync(code, "FT-INV2", "{\"inv\":2}");

        Assert.Equal(WebhookApplyOutcome.InvoiceSettled, first);
        Assert.Equal(WebhookApplyOutcome.AlreadyProcessed, second);   // terminal → idempotent no-op

        using var read = tdb.NewContext();
        Assert.Equal(InvoiceStatus.Paid, (await read.Invoices.SingleAsync(i => i.Id == invoice.Id)).Status);
        Assert.Equal(OrderStatus.Paid, (await read.Orders.SingleAsync(o => o.PayosOrderCode == code)).Status);
        Assert.Equal(4, (await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId)).RemainingCredits); // KHÔNG cộng
        Assert.Equal(0, await read.CreditTransactions.CountAsync());  // KHÔNG ghi sổ cái credit
        Assert.Equal(1, await read.PaymentTransactions.CountAsync()); // chỉ lần 1 ghi log (lần 2 no-op trước ghi)
    }
}
