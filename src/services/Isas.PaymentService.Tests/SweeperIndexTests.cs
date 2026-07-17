using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// DB5 — index cho 2 reconciler background quét credit_reservations không còn full-scan:
///   • CreditReservationReconciler: CountAsync(owner_type, owner_id, status=Reserved) per-account.
///   • OrphanReservationReconciler: Where(status=Reserved, created_at &lt; cutoff).OrderBy(created_at).
/// Partial index (owner_type, owner_id, created_at) WHERE status='Reserved' phủ CẢ HAI.
///
/// Soi MODEL (không phụ thuộc plan thật) + PaymentTestDb dùng EnsureCreated → SQLite thực thi DDL
/// partial-index (bắt lỗi tên cột snake_case / literal enum sai). Filter literal PHẢI khớp đúng
/// chuỗi enum lưu (Status = HasConversion&lt;string&gt; → member name "Reserved") + cột snake_case 'status'.
/// </summary>
public class SweeperIndexTests
{
    private const string ExpectedFilter = "status = 'Reserved'";

    private static IReadOnlyIndex FindSweeperIndex(PaymentDbContext db)
    {
        var entity = db.Model.FindEntityType(typeof(CreditReservation))!;
        // Index composite (OwnerType, OwnerId, CreatedAt) — đúng thứ tự cột.
        return entity.GetIndexes().Single(ix =>
            ix.Properties.Count == 3 &&
            ix.Properties[0].Name == nameof(CreditReservation.OwnerType) &&
            ix.Properties[1].Name == nameof(CreditReservation.OwnerId) &&
            ix.Properties[2].Name == nameof(CreditReservation.CreatedAt));
    }

    [Fact]
    public void SweeperIndex_TonTai_TrenModel_DungThuTuCot()
    {
        using var t = new PaymentTestDb();   // EnsureCreated → SQLite dựng partial-index thật
        var ix = FindSweeperIndex(t.Db);

        Assert.NotNull(ix);
        Assert.Equal("ix_credit_reservations_reserved", ix.GetDatabaseName());
    }

    [Fact]
    public void SweeperIndex_LaPartial_FilterKhopChuoiEnumLuu()
    {
        using var t = new PaymentTestDb();
        var ix = FindSweeperIndex(t.Db);

        // Filter partial PHẢI đúng literal: cột snake_case 'status' + chuỗi enum "Reserved".
        Assert.Equal(ExpectedFilter, ix.GetFilter());
    }

    [Fact]
    public void FkSupportIndex_OwnerTypeOwnerId_VanGiu_KhongBiDrop()
    {
        // DB5 additive: index partial có prefix (owner_type, owner_id) trùng FK convention index →
        // giữ FK index đầy đủ (khai tường minh, không để EF drop coi-thừa). Partial ≠ phủ FK.
        using var t = new PaymentTestDb();
        var entity = t.Db.Model.FindEntityType(typeof(CreditReservation))!;

        var fkIndex = entity.GetIndexes().SingleOrDefault(ix =>
            ix.Properties.Count == 2 &&
            ix.Properties[0].Name == nameof(CreditReservation.OwnerType) &&
            ix.Properties[1].Name == nameof(CreditReservation.OwnerId));

        Assert.NotNull(fkIndex);
        Assert.Null(fkIndex!.GetFilter());   // FK index đầy đủ (KHÔNG partial)
    }
}
