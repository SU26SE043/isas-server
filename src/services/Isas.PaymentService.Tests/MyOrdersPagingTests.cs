using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PayOS;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Keyset paging + lọc status cho <c>GET /order/my-orders</c> (trang đơn hàng của CHÍNH chủ ví).
///
/// Vì sao cần: mỗi lần bấm checkout là INSERT 1 row `orders` (ý định trả tiền, KHÔNG phải trả xong)
/// → đơn Pending bỏ dở tích lại vĩnh viễn, không job nào dọn. Endpoint tương tác này trước đây trả
/// TOÀN BỘ set, không cap.
///
/// ⚠ Đây là endpoint TIỀN: cursor chỉ mang (created_at, id), KHÔNG mang owner — nên vị ngữ owner
/// phải vô điều kiện. <see cref="MyOrders_OwnerIsolation_DonChuViKhac_KhongLotSangTrangNao"/> khoá
/// đúng ca đó (mutation-check: gỡ vị ngữ owner khỏi production → test ĐỎ).
/// Seed/dựng service theo mẫu <c>AdminOrdersOversightTests</c>.
/// </summary>
public class MyOrdersPagingTests
{
    private static OrderService NewService(PaymentTestDb tdb) =>
        new(tdb.Db,
            new PayOSClient(new PayOSOptions { ClientId = "x", ApiKey = "x", ChecksumKey = "x" }),
            Options.Create(new PayOSSettings()),
            new OrderCodeGenerator(tdb.Db));

    private static async Task<Order> SeedAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, OrderStatus status, long orderCode,
        DateTime? createdAt = null)
    {
        var created = createdAt ?? DateTime.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            Kind = OrderKind.CreditPack,
            PackageId = null,   // tránh FK ProductPackages (test paging không cần package thật)
            Status = status,
            AmountVnd = 100_000,
            PayosOrderCode = orderCode,
            ExpiredAt = created.AddMinutes(30),
            PaidAt = status == OrderStatus.Paid ? created : null,
            CreatedAt = created
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();
        return order;
    }

    // ---------- Hành vi cũ giữ nguyên (backward-compat) ----------

    [Fact]
    public async Task MyOrders_KhongCursorKhongLimit_TraCaSet_CursorNull()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
            await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Pending, 6000 + i, t0.AddMinutes(i));

        var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, me, null, null, null);

        Assert.Equal(3, page.Items.Count);
        Assert.Null(page.NextCursor);   // < default limit → trang cuối, không phát cursor
        // Mới nhất trước — giữ đúng thứ tự cũ (OrderByDescending CreatedAt).
        Assert.Equal(new long[] { 6002, 6001, 6000 }, page.Items.Select(o => o.PayosOrderCode).ToArray());
    }

    [Fact]
    public async Task MyOrders_CoPackage_TraVeTenPackage()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        var package = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = "Gói luyện phỏng vấn 10 lượt",
            Type = PackageType.OneTime,
            PriceVnd = 100_000,
            InterviewCredits = 10,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.ProductPackages.Add(package);
        tdb.Db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            Kind = OrderKind.CreditPack,
            PackageId = package.Id,
            Status = OrderStatus.Paid,
            AmountVnd = package.PriceVnd,
            PayosOrderCode = 7000,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            PaidAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, ownerId, null, null, null);

        var order = Assert.Single(page.Items);
        Assert.Equal(package.Name, order.PackageName);
    }

    // ---------- Keyset ----------

    [Fact]
    public async Task MyOrders_TrangDau_DuLimit_ThiCoNextCursor()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
            await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Pending, 6100 + i, t0.AddMinutes(i));

        var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, me, null, null, 2);

        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(new long[] { 6104, 6103 }, page.Items.Select(o => o.PayosOrderCode).ToArray());
    }

    [Fact]
    public async Task MyOrders_Keyset_NoiTiepKhongTrungKhongSot_TrangCuoiCursorNull()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
            await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Pending, 6200 + i, t0.AddMinutes(i));

        var seen = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, me, null, cursor, 2);
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(o => o.PayosOrderCode));
            cursor = page.NextCursor;
            pages++;
            Assert.True(pages <= 10, "paging không dừng");
        } while (cursor is not null);

        // Mỗi row đúng một lần, mới nhất trước (không trùng, không sót); trang cuối (1 row < limit 2)
        // trả cursor null nên vòng lặp thoát.
        Assert.Equal(new long[] { 6204, 6203, 6202, 6201, 6200 }, seen.ToArray());
    }

    [Fact]
    public async Task MyOrders_Keyset_TiebreakId_KhiCreatedAtTrungNhau()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        var same = new DateTime(2026, 7, 2, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
            await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Pending, 6300 + i, same);

        var seen = new List<long>();
        string? cursor = null;
        for (var i = 0; i < 5 && (i == 0 || cursor is not null); i++)
        {
            var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, me, null, cursor, 1);
            seen.AddRange(page.Items.Select(o => o.PayosOrderCode));
            cursor = page.NextCursor;
        }

        // CreatedAt trùng cả 3 → tiebreak theo Id mới đi hết được, không kẹt vòng lặp cùng 1 row.
        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task MyOrders_CursorRac_TraTrangDau_KhongNem()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Pending, 6400);
        await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Pending, 6401);

        var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, me, null, "not-a-valid-cursor", null);

        Assert.Equal(2, page.Items.Count);   // cursor rác = trang đầu, không bao giờ 500
    }

    // ---------- Filter status ----------

    [Fact]
    public async Task MyOrders_LocStatus_ChiTraDungTrangThai()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Pending, 6500);
        await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Paid, 6501);
        await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Paid, 6502);

        var svc = NewService(tdb);

        var paid = await svc.GetOwnerOrdersAsync(OwnerType.User, me, OrderStatus.Paid, null, null);
        Assert.Equal(2, paid.Items.Count);
        Assert.All(paid.Items, o => Assert.Equal(OrderStatus.Paid, o.Status));

        var pending = await svc.GetOwnerOrdersAsync(OwnerType.User, me, OrderStatus.Pending, null, null);
        Assert.Single(pending.Items);
        Assert.Equal(6500, pending.Items[0].PayosOrderCode);
    }

    [Fact]
    public async Task MyOrders_LocStatus_DayXuongSQL_TruocKhiCatTrang()
    {
        // Lọc SAU khi cắt trang sẽ trả trang thiếu/rỗng sai: ở đây 3 đơn Pending là 3 đơn CŨ NHẤT,
        // nên nếu lọc client-side sau Take(2) thì trang đầu (2 đơn Paid mới nhất) ra RỖNG.
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
            await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Pending, 6600 + i, t0.AddMinutes(i));
        for (var i = 0; i < 2; i++)
            await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Paid, 6610 + i, t0.AddMinutes(10 + i));

        var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, me, OrderStatus.Pending, null, 2);

        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, o => Assert.Equal(OrderStatus.Pending, o.Status));
    }

    // ---------- Owner isolation (ĐIỂM TIỀN — mutation-checked) ----------

    [Fact]
    public async Task MyOrders_OwnerIsolation_DonChuViKhac_KhongLotSangTrangNao()
    {
        using var tdb = new PaymentTestDb();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        // Đan xen theo thời gian: đơn của người khác nằm CHÍNH GIỮA dải keyset của tôi, nên nếu vị ngữ
        // owner bị nới thì chúng sẽ lọt ra ở trang giữa chứ không phải chỉ ở rìa.
        for (var i = 0; i < 6; i++)
        {
            await SeedAsync(tdb, OwnerType.User, me, OrderStatus.Pending, 6700 + i, t0.AddMinutes(i * 2));
            await SeedAsync(tdb, OwnerType.User, other, OrderStatus.Pending, 6800 + i, t0.AddMinutes(i * 2 + 1));
        }

        var seen = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, me, null, cursor, 2);
            seen.AddRange(page.Items.Select(o => o.PayosOrderCode));
            cursor = page.NextCursor;
            pages++;
            Assert.True(pages <= 20, "paging không dừng");
        } while (cursor is not null);

        Assert.Equal(6, seen.Count);
        Assert.All(seen, code => Assert.InRange(code, 6700, 6705));   // 68xx = của người khác
    }

    [Fact]
    public async Task MyOrders_OwnerIsolation_CungOwnerIdKhacOwnerType_KhongLot()
    {
        // owner = (owner_type, owner_id) — cùng Guid nhưng khác loại ví (Org vs User) là HAI ví khác nhau
        // (PAY-2/D15). Bỏ sót vế owner_type = lộ ví tổ chức sang cá nhân trùng id.
        using var tdb = new PaymentTestDb();
        var id = Guid.NewGuid();
        await SeedAsync(tdb, OwnerType.User, id, OrderStatus.Pending, 6900);
        await SeedAsync(tdb, OwnerType.Org, id, OrderStatus.Pending, 6901);

        var asUser = await NewService(tdb).GetOwnerOrdersAsync(OwnerType.User, id, null, null, null);

        Assert.Single(asUser.Items);
        Assert.Equal(6900, asUser.Items[0].PayosOrderCode);
    }

    // ---------- Npgsql translation (SQLite KHÔNG chứng minh được gì về Postgres) ----------

    [Fact]
    public void MyOrdersQuery_DichDuocSangNpgsql_GiuOwnerFilterVaOrderDesc()
    {
        // Test chạy trên SQLite; provider thật là Npgsql. Guid.CompareTo và so sánh timestamptz phải
        // dịch được SANG SQL trên Npgsql (không client-eval) — client-eval sẽ kéo toàn bảng về rồi mới
        // lọc, tức phân trang mất sạch tác dụng, mà không có gì báo lỗi. Mẫu: DB27 SweeperIndexTests.
        var opt = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql("Host=localhost;Database=probe;Username=x;Password=y")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new PaymentDbContext(opt);

        // ⚠ Dựng query y HỆT production: ownerType/status là BIẾN (không phải hằng enum viết thẳng).
        // Viết hằng vào lambda sẽ khiến EF render literal `owner_type = 'User'`, tức test SQL của một
        // truy vấn KHÁC với truy vấn thật — dạng test xanh mà không chứng minh gì.
        var ownerType = OwnerType.User;
        var ownerId = Guid.NewGuid();
        OrderStatus? status = OrderStatus.Pending;
        var cur = new KeysetCursor(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), Guid.NewGuid());

        var query = db.Orders.Where(o => o.OwnerType == ownerType && o.OwnerId == ownerId);
        if (status is OrderStatus s)
            query = query.Where(o => o.Status == s);
        query = query.Where(o => o.CreatedAt < cur.CreatedAt
            || (o.CreatedAt == cur.CreatedAt && o.Id.CompareTo(cur.Id) < 0));

        var sql = query
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Take(2)
            .ToQueryString();

        // Vị ngữ owner + status + keyset đều nằm TRONG SQL (không rơi về client-eval).
        Assert.Contains("o.owner_type =", sql);
        Assert.Contains("o.owner_id =", sql);
        Assert.Contains("o.status =", sql);
        // Keyset dịch trọn vẹn: Guid.CompareTo phải thành so sánh uuid `o.id < @…`, KHÔNG phải gọi
        // hàm CompareTo trong bộ nhớ (client-eval sẽ kéo cả bảng về rồi mới cắt ⇒ phân trang vô nghĩa).
        Assert.Contains("o.created_at < ", sql);
        Assert.Contains("o.id < ", sql);
        Assert.DoesNotContain("CompareTo", sql);
        // Thứ tự keyset (created_at DESC, id DESC) — khớp index ix_orders_owner_created; cắt trang ở SQL.
        Assert.Contains("ORDER BY o.created_at DESC, o.id DESC", sql);
        Assert.Contains("LIMIT", sql);
    }
}
