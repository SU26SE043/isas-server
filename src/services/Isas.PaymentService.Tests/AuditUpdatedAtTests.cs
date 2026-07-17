using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// DB14 — audit updated_at (đóng dấu tự động khi bản ghi bị SỬA) + type tweaks (orders.status varchar(20),
// product_packages.type lưu STRING theo GEN-2). Kiểm cả 2 đường ghi:
//   • tracked SaveChanges → override PaymentDbContext stamp updated_at.
//   • set-based ExecuteUpdate (flip status) → phải tự stamp qua .SetProperty(UpdatedAt) trong service.
public class AuditUpdatedAtTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddMinutes(-30);

    // (1) TRACKED SaveChanges: sửa entity IHasUpdatedAt (Modified) → override đóng dấu updated_at mới.
    //     Insert (Added) KHÔNG stamp → giữ nguyên giá trị đặt lúc tạo (kiểm gián tiếp: Old bảo toàn tới lúc sửa).
    [Fact]
    public async Task TrackedUpdate_StampsUpdatedAt()
    {
        using var tdb = new PaymentTestDb();
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = "Starter",
            Type = PackageType.OneTime,
            PriceVnd = 100_000,
            InterviewCredits = 5,
            IsActive = true,
            CreatedAt = Old,
            UpdatedAt = Old
        };
        tdb.Db.ProductPackages.Add(pkg);
        await tdb.Db.SaveChangesAsync();   // Added → override KHÔNG stamp → updated_at giữ Old

        using (var ctx = tdb.NewContext())
        {
            var loaded = await ctx.ProductPackages.SingleAsync(p => p.Id == pkg.Id);
            Assert.True(loaded.UpdatedAt < Old.AddMinutes(1),
                "insert (Added) KHÔNG được stamp updated_at về now");   // vẫn ~Old
            loaded.Name = "Starter+";        // Modified
            await ctx.SaveChangesAsync();    // override stamp updated_at = now
        }

        using var read = tdb.NewContext();
        var after = await read.ProductPackages.AsNoTracking().SingleAsync(p => p.Id == pkg.Id);
        Assert.True(after.UpdatedAt > Old.AddMinutes(1),
            $"updated_at phải tiến lên sau tracked update (Old={Old:o}, now={after.UpdatedAt:o})");
    }

    // (2) EXECUTE-UPDATE: ConsumeAsync flip credit_reservations.status Reserved→Consumed bằng ExecuteUpdate
    //     (bỏ qua SaveChanges override) → .SetProperty(UpdatedAt) trong CreditAccountService phải đẩy updated_at.
    [Fact]
    public async Task ExecuteUpdate_ConsumeFlip_StampsReservationUpdatedAt()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = owner,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 0,
            ReservedCredits = 1,
            UpdatedAt = Old
        });
        tdb.Db.CreditReservations.Add(new CreditReservation
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = owner,
            SessionId = sessionId,
            Status = ReservationStatus.Reserved,
            CreatedAt = Old,
            UpdatedAt = Old        // giá trị cũ để chứng minh ExecuteUpdate đẩy lên
        });
        await tdb.Db.SaveChangesAsync();

        var result = await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);
        Assert.Equal(ConsumeOutcome.Consumed, result.Outcome);

        using var read = tdb.NewContext();
        var res = await read.CreditReservations.AsNoTracking().SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Consumed, res.Status);
        Assert.True(res.UpdatedAt > Old.AddMinutes(1),
            $"updated_at reservation phải tiến lên sau ExecuteUpdate consume (Old={Old:o}, now={res.UpdatedAt:o})");
    }

    // (3) EXECUTE-UPDATE: webhook Paid flip orders.status Pending→Paid bằng ExecuteUpdate → phải stamp updated_at.
    [Fact]
    public async Task ExecuteUpdate_WebhookPaid_StampsOrderUpdatedAt()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        const long payosCode = 987654321L;

        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = "Pack5",
            Type = PackageType.OneTime,
            PriceVnd = 50_000,
            InterviewCredits = 5,
            IsActive = true,
            CreatedAt = Old,
            UpdatedAt = Old
        };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = owner,
            Kind = OrderKind.CreditPack,
            PackageId = pkg.Id,
            AmountVnd = 50_000,
            PayosOrderCode = payosCode,
            Status = OrderStatus.Pending,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = Old,
            UpdatedAt = Old
        };
        tdb.Db.ProductPackages.Add(pkg);
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();

        // WebhookService + CreditAccountService dùng CHUNG DbContext (như scope production) → transaction OK.
        var ctx = tdb.NewContext();
        var webhook = new WebhookService(ctx, new CreditAccountService(ctx));
        await webhook.ApplyPaidWebhookAsync(payosCode, "txn-1", "{}");

        using var read = tdb.NewContext();
        var after = await read.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Paid, after.Status);
        Assert.True(after.UpdatedAt > Old.AddMinutes(1),
            $"updated_at order phải tiến lên sau ExecuteUpdate webhook Paid (Old={Old:o}, now={after.UpdatedAt:o})");
    }

    // (4) TYPE TWEAKS round-trip: orders.status và product_packages.type lưu STRING (không phải int).
    //     Đọc RAW qua SQLite (bỏ qua conversion của EF) → thấy tên enum, không phải số.
    [Fact]
    public async Task TypeAndStatus_StoredAsStrings()
    {
        using var tdb = new PaymentTestDb();

        tdb.Db.ProductPackages.Add(new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = "Sub",
            Type = PackageType.Subscription,
            PriceVnd = 1,
            DurationDays = 30,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        tdb.Db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = Guid.NewGuid(),
            Kind = OrderKind.CreditPack,
            AmountVnd = 1,
            PayosOrderCode = 111,
            Status = OrderStatus.Paid,
            ExpiredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        // 1 row / bảng trên DB in-memory mới → không cần WHERE (tránh so Guid-string SQLite).
        Assert.Equal("Subscription", await RawScalarAsync(tdb, "SELECT type FROM product_packages"));
        Assert.Equal("Paid", await RawScalarAsync(tdb, "SELECT status FROM orders"));
    }

    private static async Task<string?> RawScalarAsync(PaymentTestDb tdb, string sql)
    {
        using var cmd = tdb.Connection.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return value?.ToString();
    }
}
