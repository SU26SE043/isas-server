using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// DB9 — khoá thiết kế FK nội-service: credit_reservations / credit_transactions / invoices
/// tham chiếu credit_accounts qua composite (owner_type, owner_id). Principal = ALTERNATE KEY
/// trên credit_accounts (Postgres FK không ref được unique index → phải là UNIQUE CONSTRAINT).
/// Cascade = Restrict (owner NOT NULL không SetNull; ví không bao giờ bị xoá).
///
/// Test soi MODEL (không phụ thuộc SQLite enforce FK — SQLite ở PaymentTestDb tắt PRAGMA
/// foreign_keys nên 23503 chỉ verify được trên Postgres). Ref XUYÊN service (session_id) KHÔNG FK.
/// </summary>
public class IntraServiceForeignKeyTests
{
    private static IForeignKey FindOwnerFkToAccount(PaymentDbContext db, Type dependent)
    {
        var entity = db.Model.FindEntityType(dependent)!;
        // FK composite (OwnerType, OwnerId) trỏ tới CreditAccount.
        return entity.GetForeignKeys().Single(fk =>
            fk.PrincipalEntityType.ClrType == typeof(CreditAccount) &&
            fk.Properties.Count == 2 &&
            fk.Properties.Any(p => p.Name == nameof(CreditReservation.OwnerType)) &&
            fk.Properties.Any(p => p.Name == nameof(CreditReservation.OwnerId)));
    }

    [Fact]
    public void CreditAccount_CoAlternateKey_OwnerTypeOwnerId()
    {
        using var t = new PaymentTestDb();
        var account = t.Db.Model.FindEntityType(typeof(CreditAccount))!;

        // Alternate key (owner_type, owner_id) — khác primary key (Id) — để làm principal cho 3 FK.
        var altKey = account.GetKeys().SingleOrDefault(k => !k.IsPrimaryKey()
            && k.Properties.Count == 2
            && k.Properties.Any(p => p.Name == nameof(CreditAccount.OwnerType))
            && k.Properties.Any(p => p.Name == nameof(CreditAccount.OwnerId)));

        Assert.NotNull(altKey);
    }

    [Theory]
    [InlineData(typeof(CreditReservation))]
    [InlineData(typeof(CreditTransaction))]
    [InlineData(typeof(Invoice))]
    public void Entity_CoCompositeFk_ToCreditAccount_Restrict(Type dependent)
    {
        using var t = new PaymentTestDb();
        var fk = FindOwnerFkToAccount(t.Db, dependent);

        // Principal key = alternate key trên CreditAccount (KHÔNG phải PK Id), composite owner.
        Assert.False(fk.PrincipalKey.IsPrimaryKey());
        Assert.Equal(
            new[] { nameof(CreditAccount.OwnerId), nameof(CreditAccount.OwnerType) },
            fk.PrincipalKey.Properties.Select(p => p.Name).OrderBy(n => n).ToArray());

        // Cascade = Restrict (owner NOT NULL, ví không bị xoá).
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void SessionId_KhongFk_XuyenService()
    {
        using var t = new PaymentTestDb();

        // session_id (CreditReservation) là ref XUYÊN service → InterviewService: giữ Guid lỏng (GEN-2),
        // KHÔNG có FK nào trong Payment DB tham chiếu qua session_id.
        var reservation = t.Db.Model.FindEntityType(typeof(CreditReservation))!;
        Assert.DoesNotContain(reservation.GetForeignKeys(),
            fk => fk.Properties.Any(p => p.Name == nameof(CreditReservation.SessionId)));
    }
}
