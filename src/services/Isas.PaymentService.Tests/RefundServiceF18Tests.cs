using System.Data.Common;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// F18 — hoàn tiền / đảo giao dịch. Trước vòng này <c>CreditTransactionReason.Refund</c> là enum CHẾT
/// (0 chỗ ghi trong toàn <c>src/</c>).
///
/// Bất biến được canh xuyên suốt: <c>remaining + reserved = Σ delta</c> cho mỗi ví — máy dò drift duy
/// nhất của hệ thống. Mỗi test hoàn tiền đều kiểm lại nó SAU thao tác, không chỉ kiểm số dư.
/// </summary>
public class RefundServiceF18Tests
{
    private static readonly Guid Admin = Guid.NewGuid();

    // ── seed helpers ─────────────────────────────────────────────────────────────────────────

    private static async Task<(Order Order, CreditTransaction? Purchase, CreditAccount Account)> SeedPaidPurchaseAsync(
        PaymentTestDb tdb,
        int purchasedCredits,
        int remaining,
        int reserved = 0,
        int freeGranted = 0,
        int alreadyConsumed = 0,
        OrderKind kind = OrderKind.CreditPack,
        OrderStatus status = OrderStatus.Paid,
        bool writePurchaseLedger = true)
    {
        var ownerId = Guid.NewGuid();

        var account = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = reserved,
            FreeCreditsGranted = freeGranted,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(account);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            Kind = kind,
            Status = status,
            AmountVnd = 500_000,
            PayosOrderCode = Random.Shared.NextInt64(1, long.MaxValue / 4),
            ExpiredAt = DateTime.UtcNow.AddHours(1),
            PaidAt = status == OrderStatus.Paid ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow
        };
        tdb.Db.Orders.Add(order);
        await tdb.Db.SaveChangesAsync();

        // Sổ cái dựng lại đúng lịch sử của ví: quà (nếu có) → mua → tiêu.
        if (freeGranted > 0)
            tdb.Db.CreditTransactions.Add(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                OwnerType = OwnerType.User,
                OwnerId = ownerId,
                Delta = freeGranted,
                Reason = CreditTransactionReason.FreeGrant,
                CreatedAt = DateTime.UtcNow.AddMinutes(-30)
            });

        CreditTransaction? purchase = null;
        if (writePurchaseLedger && purchasedCredits > 0)
        {
            purchase = new CreditTransaction
            {
                Id = Guid.NewGuid(),
                OwnerType = OwnerType.User,
                OwnerId = ownerId,
                OrderId = order.Id,
                Delta = purchasedCredits,
                Reason = CreditTransactionReason.Purchase,
                CreatedAt = DateTime.UtcNow.AddMinutes(-20)
            };
            tdb.Db.CreditTransactions.Add(purchase);
        }

        for (var i = 0; i < alreadyConsumed; i++)
            tdb.Db.CreditTransactions.Add(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                OwnerType = OwnerType.User,
                OwnerId = ownerId,
                SessionId = Guid.NewGuid(),
                Delta = -1,
                Reason = CreditTransactionReason.Consume,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10 + i)
            });

        await tdb.Db.SaveChangesAsync();
        return (order, purchase, account);
    }

    /// <summary>Bất biến sổ cái: <c>remaining + reserved = Σ delta</c>.</summary>
    private static async Task AssertLedgerInvariantAsync(PaymentTestDb tdb, Guid ownerId)
    {
        await using var db = tdb.NewContext();
        var acc = await db.CreditAccounts.AsNoTracking()
            .SingleAsync(a => a.OwnerType == OwnerType.User && a.OwnerId == ownerId);
        var sum = await db.CreditTransactions
            .Where(t => t.OwnerType == OwnerType.User && t.OwnerId == ownerId)
            .SumAsync(t => (int?)t.Delta) ?? 0;

        Assert.Equal(sum, acc.RemainingCredits + acc.ReservedCredits);
    }

    private static RefundService NewService(PaymentTestDb tdb) => new(tdb.Db);

    // ── happy path ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hoan_DonMuaCredit_LatRefunded_ThuHoiCredit_VaGhiButToanDaoGanKhoanMuaGoc()
    {
        using var tdb = new PaymentTestDb();
        var (order, purchase, acc) = await SeedPaidPurchaseAsync(tdb, purchasedCredits: 5, remaining: 5);

        var result = await NewService(tdb).RefundOrderAsync(
            order.Id, Admin, "khách đổi ý", "PAYOS-RF-1", allowPartialClawback: false);

        Assert.Equal(RefundOutcome.Refunded, result.Outcome);
        Assert.Equal(5, result.CreditsPurchased);
        Assert.Equal(5, result.CreditsClawedBack);

        await using var db = tdb.NewContext();

        var after = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Refunded, after.Status);
        Assert.Equal(Admin, after.RefundedBy);
        Assert.Equal("khách đổi ý", after.RefundReason);
        Assert.Equal("PAYOS-RF-1", after.RefundGatewayRef);
        Assert.NotNull(after.RefundedAt);

        var refundTx = await db.CreditTransactions.AsNoTracking()
            .SingleAsync(t => t.Reason == CreditTransactionReason.Refund);
        Assert.Equal(-5, refundTx.Delta);
        Assert.Equal(order.Id, refundTx.OrderId);
        // Liên kết tới bút toán gốc — thứ mà trước F18 hoàn toàn không tồn tại.
        Assert.Equal(purchase!.Id, refundTx.ReversesTransactionId);

        var wallet = await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id);
        Assert.Equal(0, wallet.RemainingCredits);

        await AssertLedgerInvariantAsync(tdb, order.OwnerId);
    }

    [Fact]
    public async Task Hoan_HaiLan_LaIdempotent_KhongTruViLanHai()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, acc) = await SeedPaidPurchaseAsync(tdb, purchasedCredits: 5, remaining: 5);

        var svc = NewService(tdb);
        await svc.RefundOrderAsync(order.Id, Admin, "lần 1", null, false);
        var second = await svc.RefundOrderAsync(order.Id, Admin, "lần 2", null, false);

        Assert.Equal(RefundOutcome.AlreadyRefunded, second.Outcome);

        await using var db = tdb.NewContext();
        Assert.Equal(1, await db.CreditTransactions.CountAsync(t => t.Reason == CreditTransactionReason.Refund));
        Assert.Equal(0, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id)).RemainingCredits);

        await AssertLedgerInvariantAsync(tdb, order.OwnerId);
    }

    /// <summary>
    /// Khoá idempotency THẬT nằm ở UNIQUE(reverses_transaction_id), không phải ở câu check-then-act đầu
    /// hàm. Dựng ca "bút toán đảo đã tồn tại nhưng đơn vẫn Paid" (hình dạng của một request đua vừa ghi
    /// sổ xong) → lần hoàn này phải bị DB chặn, ví KHÔNG bị trừ hai lần.
    /// </summary>
    [Fact]
    public async Task Hoan_KhiKhoanMuaDaCoButToanDao_BiDbChan_ViKhongBiTruHaiLan()
    {
        using var tdb = new PaymentTestDb();
        var (order, purchase, acc) = await SeedPaidPurchaseAsync(tdb, purchasedCredits: 5, remaining: 5);

        tdb.Db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = order.OwnerId,
            OrderId = order.Id,
            Delta = -5,
            Reason = CreditTransactionReason.Refund,
            ReversesTransactionId = purchase!.Id,
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var result = await NewService(tdb).RefundOrderAsync(order.Id, Admin, "đua", null, false);

        Assert.Equal(RefundOutcome.AlreadyRefunded, result.Outcome);

        await using var db = tdb.NewContext();
        // Trừ ví phải bị cuốn theo rollback — không được trừ thêm lần nữa.
        Assert.Equal(5, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id)).RemainingCredits);
        Assert.Equal(1, await db.CreditTransactions.CountAsync(t => t.Reason == CreditTransactionReason.Refund));
        // Đơn cũng không được lật (transaction rollback nguyên khối).
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    // ── bảo vệ credit tặng ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ví được tặng 3 (F7) + mua 5, chưa tiêu gì ⇒ remaining 8. Hoàn khoản mua chỉ được lấy lại 5;
    /// 3 credit quà PHẢI còn nguyên. Không có phép trừ phần quà thì trần thu hồi là 8 và người dùng
    /// mất cả suất dùng thử — tức hoàn tiền biến quà thành tiền mặt.
    /// </summary>
    [Fact]
    public async Task Hoan_KhongDuocAnVaoSuatDungThuChuaTieu()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, acc) = await SeedPaidPurchaseAsync(
            tdb, purchasedCredits: 5, remaining: 8, freeGranted: 3);

        var result = await NewService(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null, false);

        Assert.Equal(RefundOutcome.Refunded, result.Outcome);
        Assert.Equal(5, result.ClawbackCeiling);
        Assert.Equal(5, result.CreditsClawedBack);

        await using var db = tdb.NewContext();
        var wallet = await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id);
        Assert.Equal(3, wallet.RemainingCredits);   // đúng phần quà, còn nguyên

        await AssertLedgerInvariantAsync(tdb, order.OwnerId);
    }

    /// <summary>
    /// Cùng ví đó nhưng đã tiêu 5 lượt ⇒ quà đã tiêu hết (quà tiêu TRƯỚC), 3 credit còn lại là credit đã
    /// trả tiền ⇒ trần thu hồi = 3, không còn gì phải bảo vệ. Ca này khoá chiều ngược lại: bảo vệ quà
    /// không được biến thành "vĩnh viễn không thu hồi được 3 credit".
    /// </summary>
    [Fact]
    public async Task Hoan_KhiQuaDaTieuHet_ThuHoiDuocPhanCreditDaTraTienConLai()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, acc) = await SeedPaidPurchaseAsync(
            tdb, purchasedCredits: 5, remaining: 3, freeGranted: 3, alreadyConsumed: 5);

        var result = await NewService(tdb).RefundOrderAsync(
            order.Id, Admin, "lý do", null, allowPartialClawback: true);

        Assert.Equal(RefundOutcome.Refunded, result.Outcome);
        Assert.Equal(3, result.ClawbackCeiling);
        Assert.Equal(3, result.CreditsClawedBack);

        await using var db = tdb.NewContext();
        Assert.Equal(0, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id)).RemainingCredits);

        await AssertLedgerInvariantAsync(tdb, order.OwnerId);
    }

    // ── thu hồi thiếu ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hoan_KhiViDaTieuBot_MacDinh_TuChoi_VaKhongDoiGiCa()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, acc) = await SeedPaidPurchaseAsync(
            tdb, purchasedCredits: 5, remaining: 2, alreadyConsumed: 3);

        var result = await NewService(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null,
            allowPartialClawback: false);

        Assert.Equal(RefundOutcome.InsufficientCredits, result.Outcome);
        Assert.Equal(5, result.CreditsPurchased);
        Assert.Equal(2, result.CreditsClawedBack);   // số thu hồi ĐƯỢC, để admin quyết

        await using var db = tdb.NewContext();
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
        Assert.Equal(2, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id)).RemainingCredits);
        Assert.False(await db.CreditTransactions.AnyAsync(t => t.Reason == CreditTransactionReason.Refund));
    }

    [Fact]
    public async Task Hoan_MotPhan_KhiAdminChapNhan_TruDungPhanThuHoiDuoc()
    {
        using var tdb = new PaymentTestDb();
        var (order, purchase, acc) = await SeedPaidPurchaseAsync(
            tdb, purchasedCredits: 5, remaining: 2, alreadyConsumed: 3);

        var result = await NewService(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null,
            allowPartialClawback: true);

        Assert.Equal(RefundOutcome.Refunded, result.Outcome);
        Assert.Equal(2, result.CreditsClawedBack);

        await using var db = tdb.NewContext();
        Assert.Equal(OrderStatus.Refunded, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
        Assert.Equal(0, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id)).RemainingCredits);

        var refundTx = await db.CreditTransactions.AsNoTracking()
            .SingleAsync(t => t.Reason == CreditTransactionReason.Refund);
        // Ledger ghi ĐÚNG phần trừ thật (−2), KHÔNG ghi −5 "cho khớp đơn": ghi −5 sẽ làm
        // Σ delta lệch số dư ⇒ gãy bất biến ⇒ mất máy dò drift.
        Assert.Equal(-2, refundTx.Delta);
        Assert.Equal(purchase!.Id, refundTx.ReversesTransactionId);

        await AssertLedgerInvariantAsync(tdb, order.OwnerId);
    }

    /// <summary>
    /// Ví đã tiêu sạch ⇒ thu hồi 0. KHÔNG được ghi ledger delta 0 (vi phạm CHECK
    /// <c>ck_credit_transactions_delta_nonzero</c> → SaveChanges ném → rollback → đơn kẹt Paid vĩnh viễn,
    /// đúng hình dạng DB20). Đơn vẫn phải lật Refunded vì tiền trả lại là sự thật.
    /// </summary>
    [Fact]
    public async Task Hoan_KhiKhongThuHoiDuocCreditNao_VanLatRefunded_VaKhongGhiLedgerDelta0()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, _) = await SeedPaidPurchaseAsync(
            tdb, purchasedCredits: 5, remaining: 0, alreadyConsumed: 5);

        var result = await NewService(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null,
            allowPartialClawback: true);

        Assert.Equal(RefundOutcome.Refunded, result.Outcome);
        Assert.Equal(0, result.CreditsClawedBack);
        Assert.Null(result.RefundTransactionId);

        await using var db = tdb.NewContext();
        Assert.Equal(OrderStatus.Refunded, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
        Assert.False(await db.CreditTransactions.AnyAsync(t => t.Reason == CreditTransactionReason.Refund));

        await AssertLedgerInvariantAsync(tdb, order.OwnerId);
    }

    // ── credit đang giữ ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PAY-12 — credit đang <c>reserved</c> thuộc về buổi thi ĐANG diễn ra. Hoàn tiền chỉ được đụng
    /// <c>remaining</c>; chạm vào reserved là văng người đang thi giữa chừng.
    /// </summary>
    [Fact]
    public async Task Hoan_KhongDungVaoCreditDangGiuChoBuoiThiDangChay()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, acc) = await SeedPaidPurchaseAsync(
            tdb, purchasedCredits: 5, remaining: 2, reserved: 3);

        var result = await NewService(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null,
            allowPartialClawback: true);

        Assert.Equal(2, result.ClawbackCeiling);
        Assert.Equal(2, result.CreditsClawedBack);

        await using var db = tdb.NewContext();
        var wallet = await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id);
        Assert.Equal(0, wallet.RemainingCredits);
        Assert.Equal(3, wallet.ReservedCredits);   // nguyên vẹn
    }

    // ── đua thật: số dư đổi giữa lúc tính trần và lúc trừ ────────────────────────────────────

    /// <summary>
    /// ĐUA THẬT — chen một lượt reserve vào ĐÚNG khe giữa câu đọc sổ cái (tính trần) và câu trừ ví.
    /// Trần được tính trên snapshot cũ (remaining 5) nhưng lúc trừ thì ví chỉ còn 1.
    ///   • KHÔNG guard <c>remaining &gt;= clawback</c>: phép trừ đẩy số dư xuống âm → nổ CHECK
    ///     <c>ck_credit_accounts_non_negative</c> → transaction chết → đơn KHÔNG lật được sang Refunded
    ///     dù admin bấm bao nhiêu lần (đúng lớp bug DB22, chỉ khác điểm vào).
    ///   • CÓ guard: 0 row → huỷ sạch, trả <see cref="RefundOutcome.WalletChanged"/>, đơn còn Paid, gọi lại được.
    /// </summary>
    [Fact]
    public async Task Hoan_KhiSoDuDoiGiuaChung_HuySachVaBaoGoiLai_KhongNoCheck()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, acc) = await SeedPaidPurchaseAsync(tdb, purchasedCredits: 5, remaining: 5);

        // Ngay sau câu SUM tính trần thu hồi, mô phỏng "4 credit vừa bị giữ cho các buổi thi khác":
        // remaining 5→1, reserved 0→4. Ghi bằng SQL thô trên CHÍNH connection đang mở (SQLite không cho
        // tạo DbContext khi reader còn sống) — và ghi ở đây là autocommit, nằm NGOÀI transaction hoàn
        // tiền (transaction đó mở sau), nên nó sống sót qua rollback đúng như một request thật.
        var interceptor = new RaceAfterLedgerSumInterceptor(async cmd =>
        {
            await using var bump = cmd.Connection!.CreateCommand();
            bump.CommandText =
                "UPDATE credit_accounts SET remaining_credits = 1, reserved_credits = 4;";
            await bump.ExecuteNonQueryAsync();
        });

        await using var raced = new PaymentDbContext(
            new DbContextOptionsBuilder<PaymentDbContext>()
                .UseSqlite(tdb.Connection)
                .AddInterceptors(interceptor)
                .UseSnakeCaseNamingConvention()
                .Options);

        var result = await new RefundService(raced).RefundOrderAsync(
            order.Id, Admin, "lý do", null, allowPartialClawback: true);

        Assert.True(interceptor.Fired, "Interceptor phải chen được vào giữa câu tính trần và câu trừ ví.");
        Assert.Equal(RefundOutcome.WalletChanged, result.Outcome);

        await using var db = tdb.NewContext();
        // Đơn KHÔNG được lật — nếu không thì tiền đã trả mà credit vẫn nguyên trong ví.
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
        var wallet = await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id);
        Assert.Equal(1, wallet.RemainingCredits);
        Assert.Equal(4, wallet.ReservedCredits);
        Assert.False(await db.CreditTransactions.AnyAsync(t => t.Reason == CreditTransactionReason.Refund));
    }

    /// <summary>
    /// ĐUA THẬT #2 — một admin khác hoàn xong đơn NGAY SAU khi request này đã đọc đơn (nên câu kiểm
    /// "đã Refunded chưa" ở đầu hàm nhìn thấy dữ liệu cũ và cho đi tiếp).
    ///
    /// Ca này tồn tại vì mutation-check: gỡ guard <c>WHERE status = Paid</c> khỏi câu lật trạng thái mà
    /// toàn bộ test còn lại vẫn XANH. Truy ra lý do: với <c>clawback &gt; 0</c> thì UNIQUE
    /// <c>reverses_transaction_id</c> đã chặn khoản trừ thứ hai, nên guard không phải hàng rào TIỀN.
    /// Nhưng nó là hàng rào AUDIT: không có nó, request đến sau vẫn ghi đè
    /// <c>refunded_by</c>/<c>refund_reason</c> của người đã hoàn thật, tức cột dựng ra để truy trách
    /// nhiệm lại ghi tên nhầm người. Đó là hành vi test này khoá.
    /// </summary>
    [Fact]
    public async Task Hoan_KhiAdminKhacVuaHoanSong_KhongGhiDeAuditCuaNguoiDaHoan()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, acc) = await SeedPaidPurchaseAsync(tdb, purchasedCredits: 5, remaining: 5);
        var otherAdmin = Guid.NewGuid();

        var interceptor = new RaceBeforeOrderFlipInterceptor(async cmd =>
        {
            await using var racer = cmd.Connection!.CreateCommand();
            racer.CommandText =
                "UPDATE orders SET status = 'Refunded', refunded_by = $by, refund_reason = 'người khác hoàn trước';";
            var p = racer.CreateParameter();
            p.ParameterName = "$by";
            p.Value = otherAdmin.ToString();
            racer.Parameters.Add(p);
            await racer.ExecuteNonQueryAsync();
        });

        await using var raced = new PaymentDbContext(
            new DbContextOptionsBuilder<PaymentDbContext>()
                .UseSqlite(tdb.Connection)
                .AddInterceptors(interceptor)
                .UseSnakeCaseNamingConvention()
                .Options);

        var result = await new RefundService(raced).RefundOrderAsync(
            order.Id, Admin, "tôi hoàn", null, allowPartialClawback: true);

        Assert.True(interceptor.Fired, "Interceptor phải chen được trước câu lật trạng thái đơn.");
        Assert.Equal(RefundOutcome.AlreadyRefunded, result.Outcome);

        await using var db = tdb.NewContext();
        var after = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(otherAdmin, after.RefundedBy);                      // audit của người hoàn thật
        Assert.Equal("người khác hoàn trước", after.RefundReason);
        Assert.Equal(5, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id)).RemainingCredits);
    }

    /// <summary>Chen vào ngay sau câu đọc ví (trước khi transaction hoàn tiền mở ra).</summary>
    private sealed class RaceBeforeOrderFlipInterceptor : DbCommandInterceptor
    {
        private readonly Func<DbCommand, Task> _race;
        private bool _done;

        public bool Fired => _done;

        public RaceBeforeOrderFlipInterceptor(Func<DbCommand, Task> race) => _race = race;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_done && command.CommandText.Contains("credit_accounts", StringComparison.OrdinalIgnoreCase)
                       && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                _done = true;
                await _race(command);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Chen vào ngay sau câu SUM sổ cái (bước tính trần thu hồi).</summary>
    private sealed class RaceAfterLedgerSumInterceptor : DbCommandInterceptor
    {
        private readonly Func<DbCommand, Task> _race;
        private bool _done;

        public bool Fired => _done;

        public RaceAfterLedgerSumInterceptor(Func<DbCommand, Task> race) => _race = race;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_done && command.CommandText.Contains("sum(", StringComparison.OrdinalIgnoreCase)
                       && command.CommandText.Contains("credit_transactions", StringComparison.OrdinalIgnoreCase))
            {
                _done = true;
                await _race(command);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    // ── phạm vi / trạng thái ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Failed)]
    public async Task Hoan_DonChuaPaid_BiTuChoi(OrderStatus status)
    {
        using var tdb = new PaymentTestDb();
        var (order, _, _) = await SeedPaidPurchaseAsync(
            tdb, purchasedCredits: 5, remaining: 5, status: status, writePurchaseLedger: false);

        var result = await NewService(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null, true);

        Assert.Equal(RefundOutcome.NotPaid, result.Outcome);
    }

    [Theory]
    [InlineData(OrderKind.InvoiceSettlement)]
    [InlineData(OrderKind.SubscriptionPurchase)]
    [InlineData(OrderKind.SubscriptionRenewal)]
    public async Task Hoan_LoaiDonKhongPhaiCreditPack_BiTuChoi(OrderKind kind)
    {
        using var tdb = new PaymentTestDb();
        var (order, _, _) = await SeedPaidPurchaseAsync(
            tdb, purchasedCredits: 0, remaining: 5, kind: kind, writePurchaseLedger: false);

        var result = await NewService(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null, true);

        Assert.Equal(RefundOutcome.UnsupportedKind, result.Outcome);

        await using var db = tdb.NewContext();
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task Hoan_DonKhongTonTai_TraOrderNotFound()
    {
        using var tdb = new PaymentTestDb();
        var result = await NewService(tdb).RefundOrderAsync(Guid.NewGuid(), Admin, "lý do", null, true);
        Assert.Equal(RefundOutcome.OrderNotFound, result.Outcome);
    }

    /// <summary>
    /// Đơn Paid nhưng KHÔNG có bút toán mua (đường DB20: gói không sinh credit → giữ Paid, log, không ghi
    /// sổ). Tiền đã thu thật nên vẫn hoàn được; chỉ là không có credit để thu hồi.
    /// </summary>
    [Fact]
    public async Task Hoan_DonPaidNhungChuaTungCongCredit_VanHoanDuoc()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, acc) = await SeedPaidPurchaseAsync(
            tdb, purchasedCredits: 0, remaining: 4, writePurchaseLedger: false);

        var result = await NewService(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null, false);

        Assert.Equal(RefundOutcome.Refunded, result.Outcome);
        Assert.Equal(0, result.CreditsPurchased);
        Assert.Equal(0, result.CreditsClawedBack);

        await using var db = tdb.NewContext();
        // Ví của người khác... à không, chính ví này — số dư KHÔNG được đụng tới.
        Assert.Equal(4, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id)).RemainingCredits);
    }

    /// <summary>
    /// Đơn Refunded phải nằm ngoài đường đi của MỌI cơ chế tự động: webhook Paid muộn, polling đối soát,
    /// sweeper hết hạn — cả ba đều guard <c>status == Pending</c>. Khoá bằng test để lần sau ai nới guard
    /// thành "khác Paid" thì đỏ ngay: nới như vậy sẽ cho webhook muộn cộng credit lại vào ví vừa thu hồi.
    /// </summary>
    [Fact]
    public async Task DonDaRefunded_KhongBiWebhookMuonCongLaiCredit()
    {
        using var tdb = new PaymentTestDb();
        var (order, _, acc) = await SeedPaidPurchaseAsync(tdb, purchasedCredits: 5, remaining: 5);
        await NewService(tdb).RefundOrderAsync(order.Id, Admin, "lý do", null, false);

        var webhook = new WebhookService(tdb.Db, new CreditAccountService(tdb.Db));
        var outcome = await webhook.ApplyPaidWebhookAsync(order.PayosOrderCode, "gw-late", "{}");

        Assert.Equal(WebhookApplyOutcome.AlreadyProcessed, outcome);

        await using var db = tdb.NewContext();
        Assert.Equal(0, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.Id)).RemainingCredits);
        Assert.Equal(OrderStatus.Refunded, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }
}
