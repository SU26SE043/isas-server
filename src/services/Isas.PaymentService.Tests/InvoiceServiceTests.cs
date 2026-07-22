using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Isas.Shared.Pagination;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using System.Data.Common;
using static Isas.PaymentService.DTOs.OrderRequest;
using static Isas.PaymentService.Services.IInvoiceService;

namespace Isas.PaymentService.Tests;

// P8b — InvoiceService (hóa đơn postpaid; mock IOrderService để không cần PayOS thật).
// Verify:
// (1) chốt kỳ → Invoice(Issued, interview_count=period_usage, amount=count×unit_price) + period_usage reset 0 (1 tx).
// (2) chốt kỳ không có ví → KeyNotFoundException (404).
// (3) pay (Issued) → outcome Created, Order gắn invoice_id, OrderService được gọi đúng invoice.
// (4) pay invoice không tồn tại → NotFound (404), KHÔNG gọi OrderService.
// (5) pay của chủ khác → NotFound (404, owner-scope), KHÔNG gọi OrderService.
// (6) pay đã Paid → NotPayable (409/no-op), KHÔNG gọi OrderService.
// (7) GetInvoices owner-scope: chỉ hóa đơn của chính chủ ví.
// (8) GetInvoice của chủ khác → null (404).
public class InvoiceServiceTests
{
    // Stub IOrderService — bắt invoice truyền vào CreateInvoiceSettlementOrderAsync, không đụng PayOS.
    private sealed class StubOrderService : IOrderService
    {
        public Invoice? LastInvoice { get; private set; }
        public int CallCount { get; private set; }

        public Task<OrderResponse> CreateInvoiceSettlementOrderAsync(Invoice invoice, CancellationToken ct = default)
        {
            CallCount++;
            LastInvoice = invoice;
            return Task.FromResult(new OrderResponse
            {
                Id = Guid.NewGuid(),
                OwnerType = invoice.OwnerType,
                OwnerId = invoice.OwnerId,
                Kind = OrderKind.InvoiceSettlement,
                InvoiceId = invoice.Id,
                AmountVnd = (int)invoice.Amount,
                Status = OrderStatus.Pending,
                PayosOrderCode = 260711999001,
                CheckoutUrl = "https://pay.payos.vn/web/stub"
            });
        }

        public Task<OrderResponse> CreateOrderAsync(OwnerType ownerType, Guid ownerId, CreateOrderRequest request, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<OrderResponse?> GetOrderAsync(Guid id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<KeysetPage<OrderResponse>> GetOwnerOrdersAsync(OwnerType ownerType, Guid ownerId, OrderStatus? status, string? cursor, int? limit, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<KeysetPage<OrderResponse>> ListAllOrdersAsync(OrderStatus? status, OwnerType? ownerType, string? cursor, int? limit, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task CancelOrderAsync(Guid id, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private static InvoiceService NewService(PaymentTestDb tdb, StubOrderService orders, decimal unitPrice, out PaymentDbContext ctx)
    {
        ctx = tdb.NewContext();
        var billing = Options.Create(new BillingSettings { UnitPrice = unitPrice });
        return new InvoiceService(ctx, orders, billing);
    }

    // F23/BK24 — ví Prepaid (đối lập SeedPostpaidAccountAsync) cho test guard NotPostpaid.
    private static async Task SeedPrepaidAccountAsync(PaymentTestDb tdb, Guid orgId, int periodUsage = 0)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 10,
            ReservedCredits = 0,
            PeriodUsage = periodUsage,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
    }

    private static async Task SeedPostpaidAccountAsync(
        PaymentTestDb tdb, Guid orgId, int periodUsage, int creditLimit = 100)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = PaymentMode.Postpaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            ReservedCredits = 0,
            CreditLimit = creditLimit,
            PeriodUsage = periodUsage,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
    }

    // DB9 — invoices có FK (owner_type,owner_id)→credit_accounts. Đảm bảo ví Org tồn tại (idempotent:
    // vài test seed nhiều hóa đơn cùng org → chỉ tạo 1 ví, tránh đụng UNIQUE owner). Production: hóa đơn
    // chỉ sinh từ CloseBillingPeriodAsync khi ví đã tồn tại.
    private static async Task EnsureOrgAccountAsync(PaymentTestDb tdb, Guid orgId)
    {
        if (await tdb.Db.CreditAccounts.AnyAsync(a => a.OwnerType == OwnerType.Org && a.OwnerId == orgId))
            return;
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = PaymentMode.Postpaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            ReservedCredits = 0,
            CreditLimit = 100,
            PeriodUsage = 0,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
    }

    private static async Task<Invoice> SeedInvoiceAsync(
        PaymentTestDb tdb, Guid orgId, InvoiceStatus status, int count = 5, decimal unitPrice = 50_000)
    {
        await EnsureOrgAccountAsync(tdb, orgId);
        var inv = new Invoice
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PeriodStart = DateTime.UtcNow.AddDays(-30),
            PeriodEnd = DateTime.UtcNow,
            InterviewCount = count,
            UnitPrice = unitPrice,
            Amount = count * unitPrice,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.Invoices.Add(inv);
        await tdb.Db.SaveChangesAsync();
        return inv;
    }

    // (1) Chốt kỳ → Invoice(Issued, count=usage, amount=count×unit_price) + period_usage reset 0.
    [Fact]
    public async Task Close_SnapshotUsage_TaoInvoice_ResetPeriodUsage()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, periodUsage: 7);

        var result = await NewService(tdb, new StubOrderService(), unitPrice: 50_000, out _)
            .CloseBillingPeriodAsync(orgId);

        Assert.Equal(CloseBillingPeriodOutcome.Closed, result.Outcome);
        Assert.NotNull(result.Invoice);
        var invoice = result.Invoice!;
        Assert.Equal(OwnerType.Org, invoice.OwnerType);
        Assert.Equal(orgId, invoice.OwnerId);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Equal(7, invoice.InterviewCount);
        Assert.Equal(50_000m, invoice.UnitPrice);
        Assert.Equal(350_000m, invoice.Amount);        // 7 × 50_000

        using var read = tdb.NewContext();
        var inv = await read.Invoices.SingleAsync(i => i.OwnerId == orgId);
        Assert.Equal(350_000m, inv.Amount);
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(0, acc.PeriodUsage);             // reset 0 cùng transaction
    }

    // (2) Chốt kỳ không có ví → KeyNotFoundException (404).
    [Fact]
    public async Task Close_KhongCoVi_WalletMissing()
    {
        using var tdb = new PaymentTestDb();
        var svc = NewService(tdb, new StubOrderService(), unitPrice: 50_000, out _);

        var result = await svc.CloseBillingPeriodAsync(Guid.NewGuid());

        Assert.Equal(CloseBillingPeriodOutcome.WalletMissing, result.Outcome);
        Assert.Null(result.Invoice);
    }

    // (2b) F23/BK24 — org đang Prepaid → NotPostpaid, KHÔNG tạo invoice, KHÔNG đụng period_usage.
    [Fact]
    public async Task Close_OrgPrepaid_NotPostpaid()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedPrepaidAccountAsync(tdb, orgId, periodUsage: 3);

        var result = await NewService(tdb, new StubOrderService(), unitPrice: 50_000, out _)
            .CloseBillingPeriodAsync(orgId);

        Assert.Equal(CloseBillingPeriodOutcome.NotPostpaid, result.Outcome);
        Assert.Null(result.Invoice);

        using var read = tdb.NewContext();
        Assert.False(await read.Invoices.AnyAsync(i => i.OwnerId == orgId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(3, acc.PeriodUsage);   // không bị đụng vào
    }

    // (3) Pay (Issued) → Created, Order gắn invoice_id; OrderService được gọi đúng invoice.
    [Fact]
    public async Task Pay_Issued_TaoOrderInvoiceSettlement()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var inv = await SeedInvoiceAsync(tdb, orgId, InvoiceStatus.Issued, count: 4, unitPrice: 50_000);
        var stub = new StubOrderService();

        var result = await NewService(tdb, stub, unitPrice: 50_000, out _)
            .PayInvoiceAsync(OwnerType.Org, orgId, inv.Id);

        Assert.Equal(PayInvoiceOutcome.Created, result.Outcome);
        Assert.NotNull(result.Order);
        Assert.Equal(OrderKind.InvoiceSettlement, result.Order!.Kind);
        Assert.Equal(inv.Id, result.Order.InvoiceId);
        Assert.Equal(1, stub.CallCount);
        Assert.Equal(inv.Id, stub.LastInvoice!.Id);
    }

    // (4) Pay invoice không tồn tại → NotFound (404), KHÔNG gọi OrderService.
    [Fact]
    public async Task Pay_KhongTonTai_NotFound()
    {
        using var tdb = new PaymentTestDb();
        var stub = new StubOrderService();

        var result = await NewService(tdb, stub, unitPrice: 50_000, out _)
            .PayInvoiceAsync(OwnerType.Org, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(PayInvoiceOutcome.NotFound, result.Outcome);
        Assert.Equal(0, stub.CallCount);
    }

    // (5) Pay của chủ khác → NotFound (404, owner-scope), KHÔNG gọi OrderService.
    [Fact]
    public async Task Pay_ChuKhac_NotFound()
    {
        using var tdb = new PaymentTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var inv = await SeedInvoiceAsync(tdb, orgA, InvoiceStatus.Issued);
        var stub = new StubOrderService();

        var result = await NewService(tdb, stub, unitPrice: 50_000, out _)
            .PayInvoiceAsync(OwnerType.Org, orgB, inv.Id);

        Assert.Equal(PayInvoiceOutcome.NotFound, result.Outcome);
        Assert.Equal(0, stub.CallCount);
    }

    // (6) Pay đã Paid → NotPayable (409/no-op), KHÔNG gọi OrderService.
    [Fact]
    public async Task Pay_DaPaid_NotPayable()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var inv = await SeedInvoiceAsync(tdb, orgId, InvoiceStatus.Paid);
        var stub = new StubOrderService();

        var result = await NewService(tdb, stub, unitPrice: 50_000, out _)
            .PayInvoiceAsync(OwnerType.Org, orgId, inv.Id);

        Assert.Equal(PayInvoiceOutcome.NotPayable, result.Outcome);
        Assert.Equal(0, stub.CallCount);
    }

    // (7) GetInvoices owner-scope: chỉ hóa đơn của chính chủ ví.
    [Fact]
    public async Task GetInvoices_OwnerScope()
    {
        using var tdb = new PaymentTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await SeedInvoiceAsync(tdb, orgA, InvoiceStatus.Issued);
        await SeedInvoiceAsync(tdb, orgA, InvoiceStatus.Paid);
        await SeedInvoiceAsync(tdb, orgB, InvoiceStatus.Issued);

        var list = await NewService(tdb, new StubOrderService(), unitPrice: 50_000, out _)
            .GetInvoicesAsync(OwnerType.Org, orgA);

        Assert.Equal(2, list.Count);
        Assert.All(list, i => Assert.Equal(orgA, i.OwnerId));
    }

    // (8) GetInvoice của chủ khác → null (404).
    [Fact]
    public async Task GetInvoice_ChuKhac_Null()
    {
        using var tdb = new PaymentTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var inv = await SeedInvoiceAsync(tdb, orgA, InvoiceStatus.Issued);

        var result = await NewService(tdb, new StubOrderService(), unitPrice: 50_000, out _)
            .GetInvoiceAsync(OwnerType.Org, orgB, inv.Id);

        Assert.Null(result);
    }

    // ── BK17 — chốt kỳ TRỪ-snapshot (period_usage − count), KHÔNG reset=0 (chống race consume) ────────

    // Interceptor mô phỏng RACE: bơm 1 lượt consume (period_usage+1) NGAY TRƯỚC câu UPDATE reset/subtract
    // của CloseBillingPeriodAsync (service đã snapshot count trước đó). reset=0 sẽ NUỐT lượt này (→0);
    // trừ-snapshot giữ lại: (count+1) − count = 1. Chạy trên cùng connection+transaction của service
    // (bump là DbCommand thô → KHÔNG qua EF interceptor lần nữa, không đệ quy).
    private sealed class BumpUsageBeforeResetInterceptor : DbCommandInterceptor
    {
        private bool _bumped;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;
            if (!_bumped
                && sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("credit_accounts", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("period_usage", StringComparison.OrdinalIgnoreCase))
            {
                _bumped = true;
                using var bump = command.Connection!.CreateCommand();
                bump.Transaction = command.Transaction;
                bump.CommandText = "UPDATE \"credit_accounts\" SET \"period_usage\" = \"period_usage\" + 1";
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    // (9) Consume xen giữa snapshot-read và reset → usage đó được GIỮ cho kỳ sau (không bị nuốt).
    //     reset=0 (code cũ) sẽ fail (→0); trừ-snapshot (BK17) đúng (→1).
    [Fact]
    public async Task Close_TruSnapshot_ConsumeXenGiua_GiuUsageKySau()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, periodUsage: 5);

        var conn = (SqliteConnection)tdb.Db.Database.GetDbConnection();
        var opts = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseSqlite(conn)
            .UseSnakeCaseNamingConvention()   // DB1 — khớp schema snake_case do PaymentTestDb tạo
            .AddInterceptors(new BumpUsageBeforeResetInterceptor())
            .Options;
        using var ctx = new PaymentDbContext(opts);
        var billing = Options.Create(new BillingSettings { UnitPrice = 50_000 });

        var result = await new InvoiceService(ctx, new StubOrderService(), billing)
            .CloseBillingPeriodAsync(orgId);

        Assert.Equal(5, result.InterviewCount);   // hóa đơn chốt ĐÚNG snapshot (5), không gồm lượt xen giữa

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(1, acc.PeriodUsage);         // (5+1) − 5 = 1 (không phải 0)
    }

    // (10) Chốt kỳ → phát sinh usage kỳ mới → chốt kỳ 2: hóa đơn 2 tính ĐÚNG usage kỳ mới (không cộng dồn cũ).
    [Fact]
    public async Task Close_PhatSinhUsageKyMoi_ChotKy2Dung()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, periodUsage: 5);

        var inv1 = await NewService(tdb, new StubOrderService(), unitPrice: 50_000, out _)
            .CloseBillingPeriodAsync(orgId);
        Assert.Equal(5, inv1.InterviewCount);

        // Kỳ mới phát sinh 3 lượt (period_usage 0 → 3).
        using (var c = tdb.NewContext())
        {
            await c.CreditAccounts.Where(a => a.OwnerId == orgId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.PeriodUsage, a => (int?)((a.PeriodUsage ?? 0) + 3)));
        }

        var inv2 = await NewService(tdb, new StubOrderService(), unitPrice: 50_000, out _)
            .CloseBillingPeriodAsync(orgId);
        Assert.Equal(3, inv2.InterviewCount);     // đúng usage kỳ mới, không dính 5 của kỳ trước

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(0, acc.PeriodUsage);         // 3 − 3 = 0
    }
}
