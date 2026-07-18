using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// DB26 — index cho 3 đường đọc `orders` trước đây đều seq-scan toàn bảng (bảng này CHƯA có index nào
/// trên cột owner, và sweeper hết hạn thì quét không index):
///   • GetOwnerOrdersAsync: WHERE (owner_type, owner_id) ORDER BY created_at DESC — trang đơn hàng user/org.
///   • OrderExpiryReconciler: WHERE status='Pending' AND expired_at &lt; cutoff ORDER BY expired_at — mỗi 5'.
///   • ListAllOrdersAsync (AUTH-7): keyset (created_at DESC, id DESC) — không lọc owner.
///
/// Soi MODEL (không phụ thuộc plan thật) + PaymentTestDb dùng EnsureCreated → SQLite thực thi DDL thật
/// nên bắt được tên cột snake_case sai / literal enum sai trong filter partial (mẫu DB5 SweeperIndexTests).
/// </summary>
public class OrderIndexTests
{
    /// <summary>
    /// Soi DESIGN-TIME model: <c>IsDescending</c> (chiều sắp xếp của index) KHÔNG được giữ trong model
    /// runtime read-optimized — đọc từ <c>db.Model</c> sẽ ném InvalidOperationException.
    /// </summary>
    private static IReadOnlyIndex Find(PaymentDbContext db, string name) =>
        db.GetService<IDesignTimeModel>().Model
          .FindEntityType(typeof(Order))!
          .GetIndexes()
          .Single(ix => ix.GetDatabaseName() == name);

    [Fact]
    public void OwnerIndex_DungThuTuCot_ChoLocOwnerRoiSortCreatedAt()
    {
        using var t = new PaymentTestDb();   // EnsureCreated → SQLite dựng index thật
        var ix = Find(t.Db, "ix_orders_owner_created");

        // Thứ tự PHẢI là (owner_type, owner_id) trước — hai cột lọc bằng — rồi tới cột sort.
        Assert.Collection(ix.Properties,
            p => Assert.Equal(nameof(Order.OwnerType), p.Name),
            p => Assert.Equal(nameof(Order.OwnerId), p.Name),
            p => Assert.Equal(nameof(Order.CreatedAt), p.Name),
            p => Assert.Equal(nameof(Order.Id), p.Name));
    }

    [Fact]
    public void OwnerIndex_DuoiSapGiamDan_KhopKeysetShape()
    {
        using var t = new PaymentTestDb();
        var ix = Find(t.Db, "ix_orders_owner_created");

        // (created_at DESC, id DESC) = đúng chiều ORDER BY của query, và đúng shape keyset DB8.
        Assert.Equal(new[] { false, false, true, true }, ix.IsDescending);
    }

    [Fact]
    public void SweeperIndex_LaPartial_FilterKhopChuoiEnumLuu()
    {
        using var t = new PaymentTestDb();
        var ix = Find(t.Db, "ix_orders_pending_expired_at");

        // Status = HasConversion<string> → literal PHẢI là tên enum "Pending", cột snake_case 'status'.
        Assert.Equal("status = 'Pending'", ix.GetFilter());
        Assert.Equal(nameof(Order.ExpiredAt), ix.Properties.Single().Name);
    }

    [Fact]
    public void AdminKeysetIndex_ToanBoGiamDan()
    {
        using var t = new PaymentTestDb();
        var ix = Find(t.Db, "ix_orders_created_id_desc");

        Assert.Collection(ix.Properties,
            p => Assert.Equal(nameof(Order.CreatedAt), p.Name),
            p => Assert.Equal(nameof(Order.Id), p.Name));
        // Quy ước EF: MẢNG RỖNG = tất cả cột giảm dần (migration sinh ra `descending: new bool[0]`).
        // Chấp nhận cả dạng liệt kê tường minh [true, true] để test không vỡ nếu EF đổi cách mã hoá.
        var desc = ix.IsDescending;
        Assert.NotNull(desc);
        Assert.True(desc!.Count == 0 || desc.All(d => d),
            $"index admin keyset phải giảm dần mọi cột, đang là [{string.Join(", ", desc)}]");
    }

    [Fact]
    public void PayosOrderCodeUnique_VanGiu_KhongBiDrop()
    {
        // 3 index mới đều ADDITIVE — không được vô tình làm EF drop UNIQUE payos_order_code
        // (khoá idempotency của webhook PAY-8).
        using var t = new PaymentTestDb();
        var ix = t.Db.GetService<IDesignTimeModel>().Model
                  .FindEntityType(typeof(Order))!
                  .GetIndexes()
                  .Single(i => i.Properties.Count == 1
                            && i.Properties[0].Name == nameof(Order.PayosOrderCode));

        Assert.True(ix.IsUnique);
    }

    [Fact]
    public async Task DonCuaChuVi_DocDuoc_QuaIndexMoi()
    {
        // Smoke: index mới không làm hỏng đường đọc thật (SQLite chạy DDL + query thật).
        using var t = new PaymentTestDb();
        var ownerId = Guid.NewGuid();

        for (var i = 0; i < 3; i++)
            t.Db.Orders.Add(new Order
            {
                Id = Guid.NewGuid(),
                OwnerType = OwnerType.User,
                OwnerId = ownerId,
                PayosOrderCode = 260719_000 + i,
                AmountVnd = 100_000,
                Status = OrderStatus.Pending,
                CreatedAt = DateTime.UtcNow.AddMinutes(-i),
                UpdatedAt = DateTime.UtcNow,
                ExpiredAt = DateTime.UtcNow.AddMinutes(30)
            });
        await t.Db.SaveChangesAsync();

        var rows = await t.Db.Orders
            .Where(o => o.OwnerType == OwnerType.User && o.OwnerId == ownerId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].CreatedAt >= rows[1].CreatedAt);
    }
}
