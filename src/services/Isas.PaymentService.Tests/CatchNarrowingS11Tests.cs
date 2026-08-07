using System.Data.Common;
using System.Runtime.CompilerServices;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// S11-CATCH — sáu khối <c>catch (DbUpdateException)</c> trên đường tiền từng nuốt TRỌN mọi lỗi ghi và
/// coi tất cả là "đua UNIQUE". Ba hệ quả, cả ba đều im lặng:
///
/// <list type="number">
/// <item><b>Lỗi tạm thời bị nuốt trước khi execution strategy nhìn thấy</b> ⇒
/// <c>EnableRetryOnFailure</c> (Program.cs) thành vô hiệu ở đúng những đường không được phép hỏng.</item>
/// <item><b>Lỗi thật đội lốt kết quả nghiệp vụ</b> ⇒ FK/CHECK/varchar-quá-dài hiện ra thành
/// "Sequence contains no elements" (Reserve) hoặc "đã hoàn rồi" (Refund) — chẩn đoán sai, và với
/// Refund thì khách không bao giờ nhận được tiền trong khi log nói đã trả.</item>
/// <item><b>Cửa sổ đua rộng hơn cửa sổ UNIQUE bị bỏ ngỏ</b>: <c>CreateAccountAsync</c> ném
/// <see cref="InvalidOperationException"/> (check-then-act), mà hai site webhook chỉ bắt
/// <c>DbUpdateException</c> ⇒ đối thủ commit đúng khoảng đó đẩy exception ra khỏi transaction ⇒ cú lật
/// <c>Pending→Paid</c> rollback theo ⇒ khách trả tiền mà đơn kẹt Pending (lớp lỗi DB20).</item>
/// </list>
///
/// <para><b>Cách vá:</b> HẬU KIỂM provider-agnostic — bắt lỗi, dọn tracker, rồi HỎI LẠI DB xem tiền đề
/// nghiệp vụ ("ví đã tồn tại" / "session đã có chỗ giữ" / "khoản mua đã bị đảo") có THẬT không. Có ⇒ đúng
/// là đua. Không ⇒ lỗi thật. CỐ Ý không lọc bằng <c>PostgresException{SqlState:23505}</c>: bộ lọc đó luôn
/// false trên SQLite nên nhánh sẽ không bao giờ được test chạm tới.</para>
///
/// <para><b>Bất đối xứng có chủ đích:</b> bốn site trong/quanh transaction thì <c>throw;</c>; hai site
/// tạo ví NGOÀI transaction (<c>EnsureWalletAsync</c>, <c>GrantAsync</c>) thì <c>LogError</c> + đi tiếp,
/// vì cả hai đã có hàng rào hạ cấp mềm ngay sau (402 Insufficient / WalletMissing). Ném ở đó chỉ đổi một
/// câu trả lời đúng sự thật lấy một cái 500 trên đường nóng nhất hệ thống.</para>
/// </summary>
public class CatchNarrowingS11Tests
{
    private static readonly Guid Admin = Guid.NewGuid();

    // ══ RefundService — site RefundService.cs (INSERT bút toán đảo) ═══════════════════════════════

    /// <summary>
    /// T1a — ĐUA THẬT: một admin khác kịp ghi bút toán đảo cho ĐÚNG khoản mua này, ngay sau khi ta đã
    /// tính xong trần thu hồi. Đụng UNIQUE lọc <c>ux_credit_transactions_reverses</c> ⇒ hậu kiểm phải
    /// nhận ra đây là đua và trả AlreadyRefunded (không phải ném).
    ///
    /// Chen ở câu SUM sổ cái vì nó nằm NGOÀI transaction — dòng của đối thủ phải sống sót qua cú
    /// rollback của ta, đúng như trên Postgres nơi hai request là hai kết nối khác nhau.
    /// </summary>
    [Fact]
    public async Task Hoan_DuaThat_KhoanMuaVuaBiDao_TraAlreadyRefunded()
    {
        using var tdb = new PaymentTestDb();
        var (orderId, owner, purchaseId) = await SeedPaidOrderAsync(tdb, credits: 5, remaining: 5);

        await using var rival = await NewRivalAsync(tdb);
        var race = new RaceOnceInterceptor(
            sql => sql.Contains("sum(", StringComparison.OrdinalIgnoreCase)
                   && sql.Contains("credit_transactions", StringComparison.OrdinalIgnoreCase),
            cmd => RaceAsync(rival, cmd, db => db.CreditTransactions.Add(
                RivalLedger(owner, delta: -5, CreditTransactionReason.Refund, reverses: purchaseId))));

        await using var ctx = tdb.NewContext(null, [race]);
        var result = await new RefundService(ctx)
            .RefundOrderAsync(orderId, Admin, "lý do", null, allowPartialClawback: false);

        Assert.True(race.Fired, "Đối thủ chưa chen được ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(RefundOutcome.AlreadyRefunded, result.Outcome);

        await using var db = tdb.NewContext();
        // Rollback phải sạch: đơn chưa lật, ví chưa bị trừ lần hai.
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status);
        Assert.Equal(5, (await db.CreditAccounts.AsNoTracking()
            .SingleAsync(a => a.OwnerId == owner)).RemainingCredits);
    }

    /// <summary>
    /// T1d — sau khi trả AlreadyRefunded, bút toán đảo BỊ BỎ DỞ không được phép còn kẹt trong change
    /// tracker: caller dùng lại cùng <c>DbContext</c> (nó scoped theo request) và <c>SaveChanges</c> cho
    /// một việc khác thì dòng bỏ dở sẽ được chèn theo — đụng UNIQUE
    /// <c>ux_credit_transactions_reverses</c> ⇒ một thao tác CHẲNG LIÊN QUAN nổ giữa chừng, hoặc tệ hơn
    /// là ghi thêm một bút toán hoàn thứ hai cho cùng khoản mua.
    ///
    /// <para>Ca này được thêm SAU khi mutation "bỏ <c>ChangeTracker.Clear()</c> khỏi catch của Refund"
    /// chạy qua XANH. Điều tra ra: trong phạm vi một lời gọi service thì catch đó là điểm cuối (return
    /// hoặc throw) nên rác tracker không bao giờ được flush ⇒ đúng là code phòng thủ. Nhưng "phòng thủ"
    /// chỉ đúng chừng nào caller không đụng lại context — mà đó là điều kiện không ai bảo đảm. Thay vì
    /// nhận một mutation XANH, ta khoá luôn tính chất đó lại.</para>
    /// </summary>
    [Fact]
    public async Task Hoan_SauAlreadyRefunded_KhongDeButToanBoDoKetLaiTrongTracker()
    {
        using var tdb = new PaymentTestDb();
        var (orderId, owner, purchaseId) = await SeedPaidOrderAsync(tdb, credits: 5, remaining: 5);

        await using var rival = await NewRivalAsync(tdb);
        var race = new RaceOnceInterceptor(
            sql => sql.Contains("sum(", StringComparison.OrdinalIgnoreCase)
                   && sql.Contains("credit_transactions", StringComparison.OrdinalIgnoreCase),
            cmd => RaceAsync(rival, cmd, db => db.CreditTransactions.Add(
                RivalLedger(owner, delta: -5, CreditTransactionReason.Refund, reverses: purchaseId))));

        await using var ctx = tdb.NewContext(null, [race]);
        var result = await new RefundService(ctx)
            .RefundOrderAsync(orderId, Admin, "lý do", null, allowPartialClawback: false);

        Assert.True(race.Fired, "Đối thủ chưa chen được ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(RefundOutcome.AlreadyRefunded, result.Outcome);

        // Caller dùng lại context cho việc khác — phải là no-op, không kéo theo rác nào.
        await ctx.SaveChangesAsync();

        await using var db = tdb.NewContext();
        Assert.Equal(1, await db.CreditTransactions
            .CountAsync(t => t.Reason == CreditTransactionReason.Refund));
    }

    /// <summary>
    /// T1b — lỗi ghi THẬT (không phải đua): phải ném lên cho caller. Bản cũ nuốt và trả
    /// "AlreadyRefunded" ⇒ đơn vẫn Paid, credit vẫn nguyên, mà admin được báo là đã hoàn xong.
    /// </summary>
    [Fact]
    public async Task Hoan_LoiGhiThat_KhongPhaiDua_NemLenChoCallerBiet()
    {
        using var tdb = new PaymentTestDb();
        var (orderId, owner, _) = await SeedPaidOrderAsync(tdb, credits: 5, remaining: 5);
        var fault = new ThrowOnceInterceptor("credit_transactions");

        await using var ctx = tdb.NewContext(null, [fault]);

        await Assert.ThrowsAsync<DbUpdateException>(() => new RefundService(ctx)
            .RefundOrderAsync(orderId, Admin, "lý do", null, allowPartialClawback: false));

        Assert.True(fault.Fired, "Chưa ép được lỗi ghi ⇒ phép thử không chứng minh được gì.");

        await using var db = tdb.NewContext();
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status);
        Assert.False(await db.CreditTransactions.AnyAsync(t => t.Reason == CreditTransactionReason.Refund));
    }

    /// <summary>
    /// T1c — hậu kiểm phải neo theo ĐÚNG <c>ReversesTransactionId</c> của khoản mua đang hoàn, không
    /// phải "ví này có bút toán Refund nào không".
    ///
    /// <para>Ví ở đây đã từng hoàn một đơn KHÁC (chuyện hoàn toàn bình thường với khách mua nhiều lần).
    /// Nới vị ngữ ra mức ví/lý-do sẽ khiến lỗi ghi thật của đơn thứ hai bị báo thành "đã hoàn rồi" ⇒
    /// khách KHÔNG BAO GIỜ nhận được tiền của đơn đó, mà không ai biết vì log nói đã xong.</para>
    /// </summary>
    [Fact]
    public async Task Hoan_ViDaHoanDonKHAC_LoiGhiThat_VanNemChuKhongBaoDaHoan()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        // Ví PHẢI có trước mọi bút toán: credit_transactions có FK composite (owner_type, owner_id)
        // → credit_accounts (DB9), và SQLite CÓ bật PRAGMA foreign_keys qua EF.
        await SeedWalletAsync(tdb, owner, remaining: 5);
        // Đơn A: đã mua rồi đã hoàn (bút toán đảo tồn tại, trỏ vào khoản mua của A).
        var (_, purchaseA) = await SeedOrderWithPurchaseAsync(tdb, owner, credits: 5, status: OrderStatus.Refunded);
        await SeedLedgerAsync(tdb, owner, delta: -5, CreditTransactionReason.Refund, reverses: purchaseA);
        // Đơn B: vẫn đang Paid, chưa ai hoàn.
        var (orderB, _) = await SeedOrderWithPurchaseAsync(tdb, owner, credits: 5, status: OrderStatus.Paid);

        var fault = new ThrowOnceInterceptor("credit_transactions");
        await using var ctx = tdb.NewContext(null, [fault]);

        await Assert.ThrowsAsync<DbUpdateException>(() => new RefundService(ctx)
            .RefundOrderAsync(orderB, Admin, "lý do", null, allowPartialClawback: false));

        Assert.True(fault.Fired, "Chưa ép được lỗi ghi ⇒ phép thử không chứng minh được gì.");

        await using var db = tdb.NewContext();
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderB)).Status);
    }

    // ══ CreditAccountService.ReserveAsync — site INSERT credit_reservations ═══════════════════════

    /// <summary>
    /// T2a — ĐUA THẬT trên UNIQUE(session_id): request khác giữ chỗ cho cùng buổi ngay sau khi đường
    /// tắt idempotency của ta đọc xong. Hậu kiểm tìm thấy chỗ giữ đó ⇒ AlreadyReserved (PAY-4).
    /// </summary>
    [Fact]
    public async Task Reserve_DuaThat_SessionVuaCoChoGiu_TraAlreadyReserved()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        var session = Guid.NewGuid();
        await SeedWalletAsync(tdb, owner, remaining: 3);

        await using var rival = await NewRivalAsync(tdb);
        var race = new RaceOnceInterceptor(
            sql => sql.Contains("credit_reservations", StringComparison.OrdinalIgnoreCase)
                   && sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase),
            cmd => RaceAsync(rival, cmd, db => db.CreditReservations.Add(RivalReservation(owner, session))));

        await using var ctx = tdb.NewContext(null, [race]);
        var result = await new CreditAccountService(ctx).ReserveAsync(OwnerType.User, owner, session);

        Assert.True(race.Fired, "Đối thủ chưa chen được ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(ReserveOutcome.AlreadyReserved, result.Outcome);

        await using var db = tdb.NewContext();
        Assert.Equal(1, await db.CreditReservations.CountAsync(r => r.SessionId == session));
        // Không được trừ ví: chỗ giữ là của request khác.
        Assert.Equal(3, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == owner)).RemainingCredits);
    }

    /// <summary>
    /// T2b — lỗi ghi THẬT ở INSERT chỗ giữ. Phải ném ĐÚNG <see cref="DbUpdateException"/>.
    ///
    /// <para>Assert đúng KIỂU chứ không chỉ "có ném": bản cũ cũng ném, nhưng ném
    /// <see cref="InvalidOperationException"/> "Sequence contains no elements" từ <c>FirstAsync</c> trên
    /// tập rỗng — tức nguyên nhân thật (FK composite DB9, CHECK enum/metered, varchar quá dài) bị thay
    /// bằng một thông báo chẳng liên quan, ngay tại bước tạo buổi thi của cả B2C lẫn B2B. Chỉ assert
    /// "có ném" thì mutation đổi ngược lại vẫn XANH.</para>
    /// </summary>
    [Fact]
    public async Task Reserve_LoiGhiThat_KhongPhaiDuaSession_NemDungDbUpdateException()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, owner, remaining: 3);
        var fault = new ThrowOnceInterceptor("credit_reservations");

        await using var ctx = tdb.NewContext(null, [fault]);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => new CreditAccountService(ctx).ReserveAsync(OwnerType.User, owner, Guid.NewGuid()));

        Assert.True(fault.Fired, "Chưa ép được lỗi ghi ⇒ phép thử không chứng minh được gì.");

        await using var db = tdb.NewContext();
        Assert.Empty(await db.CreditReservations.ToListAsync());
        Assert.Equal(3, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == owner)).RemainingCredits);
    }

    // ══ WebhookService — hai site tạo ví trong transaction ════════════════════════════════════════

    /// <summary>
    /// T3 — ĐUA THẬT lúc tạo ví trên đường mua credit. Bản cũ chỉ detach <c>CreditAccount</c>, bỏ quên
    /// bút toán <c>FreeGrant +3</c> mà <c>CreateAccountAsync</c> cũng vừa Add (F7) ⇒ <c>SaveChanges</c>
    /// cuối chèn nó THẬT ⇒ sổ cái +3 trong khi số dư chỉ tăng đúng phần mua ⇒ <b>gãy bất biến
    /// remaining + reserved = Σ delta</b>, và FK không hề chặn vì ví của bên thắng đã tồn tại.
    /// </summary>
    [Fact]
    public async Task Webhook_MuaCredit_DuaTaoVi_KhongDeSotButToanFreeGrant()
    {
        using var tdb = new PaymentTestDb();
        const long code = 260808000001;
        var owner = await SeedPendingOrderAsync(tdb, code, credits: 5);

        // Chen ngay sau câu `AnyAsync` bên trong CreateAccountAsync (SELECT EXISTS) — tức sau khi nó đã
        // kết luận "chưa có ví" nhưng trước khi kịp chèn.
        await using var rival = await NewRivalAsync(tdb);
        var race = new RaceOnceInterceptor(
            sql => sql.Contains("credit_accounts", StringComparison.OrdinalIgnoreCase)
                   && sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase),
            cmd => RaceAsync(rival, cmd, db => db.CreditAccounts.Add(RivalWallet(owner))));

        await using var ctx = tdb.NewContext(null, [race]);
        var outcome = await new WebhookService(ctx, new CreditAccountService(ctx))
            .ApplyPaidWebhookAsync(code, "txn-1", "{}");

        Assert.True(race.Fired, "Đối thủ chưa chen được ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(WebhookApplyOutcome.Credited, outcome);

        await using var db = tdb.NewContext();
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(o => o.PayosOrderCode == code)).Status);
        await AssertLedgerBalancedAsync(db, owner, expectedRows: 1);   // đúng 1 dòng Purchase, KHÔNG có FreeGrant lạc
    }

    /// <summary>
    /// T4 — cùng cuộc đua nhưng đối thủ commit SỚM HƠN một nhịp: xen vào giữa <c>GetAccountAsync</c> và
    /// <c>AnyAsync</c>. Lúc đó <c>CreateAccountAsync</c> tự thấy ví đã có và ném
    /// <see cref="InvalidOperationException"/> — KHÔNG phải <c>DbUpdateException</c>.
    ///
    /// <para>Đây là ca đắt nhất: bản cũ không bắt kiểu này ⇒ exception thoát ra khỏi
    /// <c>DbRetry.RunAsync</c> ⇒ transaction rollback ⇒ cú lật <c>Pending→Paid</c> mất theo ⇒
    /// <b>khách đã trả tiền mà đơn kẹt Pending vĩnh viễn</b>, và mọi webhook redeliver sau đó lặp lại y
    /// hệt. Đúng hình dạng lỗi DB20.</para>
    /// </summary>
    [Fact]
    public async Task Webhook_MuaCredit_ViXuatHienGiuaChung_VanChotDonPaid()
    {
        using var tdb = new PaymentTestDb();
        const long code = 260808000002;
        var owner = await SeedPendingOrderAsync(tdb, code, credits: 5);

        await using var rival = await NewRivalAsync(tdb);
        var race = new RaceOnceInterceptor(
            sql => sql.Contains("credit_accounts", StringComparison.OrdinalIgnoreCase)
                   && !sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase)
                   && sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase),
            cmd => RaceAsync(rival, cmd, db => db.CreditAccounts.Add(RivalWallet(owner))));

        await using var ctx = tdb.NewContext(null, [race]);
        var outcome = await new WebhookService(ctx, new CreditAccountService(ctx))
            .ApplyPaidWebhookAsync(code, "txn-1", "{}");

        Assert.True(race.Fired, "Đối thủ chưa chen được ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(WebhookApplyOutcome.Credited, outcome);

        await using var db = tdb.NewContext();
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(o => o.PayosOrderCode == code)).Status);
        await AssertLedgerBalancedAsync(db, owner, expectedRows: 1);
    }

    /// <summary>
    /// T5 — cùng cuộc đua trên nhánh THUÊ BAO. Nhánh này không ghi bút toán credit nào cả, nên một dòng
    /// <c>FreeGrant</c> lạc vào là drift trần trụi: sổ cái +3 mà số dư đứng yên ở 0.
    /// </summary>
    [Fact]
    public async Task Webhook_ThueBao_DuaTaoVi_KhongDeSotButToanFreeGrant()
    {
        using var tdb = new PaymentTestDb();
        const long code = 260808000003;
        var owner = await SeedPendingOrderAsync(tdb, code, credits: 0, kind: OrderKind.SubscriptionPurchase);

        await using var rival = await NewRivalAsync(tdb);
        var race = new RaceOnceInterceptor(
            sql => sql.Contains("credit_accounts", StringComparison.OrdinalIgnoreCase)
                   && sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase),
            cmd => RaceAsync(rival, cmd, db => db.CreditAccounts.Add(RivalWallet(owner))));

        await using var ctx = tdb.NewContext(null, [race]);
        var outcome = await new WebhookService(ctx, new CreditAccountService(ctx))
            .ApplyPaidWebhookAsync(code, "txn-1", "{}");

        Assert.True(race.Fired, "Đối thủ chưa chen được ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(WebhookApplyOutcome.SubscriptionActivated, outcome);

        await using var db = tdb.NewContext();
        await AssertLedgerBalancedAsync(db, owner, expectedRows: 0);   // nhánh thuê bao KHÔNG ghi sổ credit
    }

    /// <summary>
    /// T8 — khoá tường minh quyết định "KHÔNG nâng <c>CreateAccountAsync</c> ra ngoài transaction".
    ///
    /// <para>Nâng ra ngoài thì hai ca dưới đây (đơn đã xử lý trước đó · gói không sinh credit) cũng sẽ
    /// tạo ví VÀ cấp 3 credit dùng thử F7 — tức đổi hành vi đường tiền cho một thứ chẳng ai yêu cầu.
    /// Không có test này thì đó chỉ là một lập luận trong đầu, không phải ràng buộc được thi hành.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]    // đơn đã Paid từ trước → AlreadyProcessed
    [InlineData(false)]   // gói không sinh credit → nhánh DB20
    public async Task Webhook_KhongDiToiBuocCongCredit_ThiKhongTuTaoVi(bool alreadyPaid)
    {
        using var tdb = new PaymentTestDb();
        const long code = 260808000004;
        var owner = await SeedPendingOrderAsync(
            tdb, code, credits: alreadyPaid ? 5 : 0, status: alreadyPaid ? OrderStatus.Paid : OrderStatus.Pending);

        await using var ctx = tdb.NewContext();
        var outcome = await new WebhookService(ctx, new CreditAccountService(ctx))
            .ApplyPaidWebhookAsync(code, "txn-1", "{}");

        Assert.Equal(alreadyPaid ? WebhookApplyOutcome.AlreadyProcessed : WebhookApplyOutcome.Credited, outcome);

        await using var db = tdb.NewContext();
        Assert.False(await db.CreditAccounts.AnyAsync(a => a.OwnerId == owner),
            "Không được tạo ví (kèm 3 credit dùng thử F7) ở nhánh không hề cộng credit.");
        Assert.Empty(await db.CreditTransactions.Where(t => t.OwnerId == owner).ToListAsync());
    }

    // ══ Bất đối xứng: hai site NGOÀI transaction phải hạ cấp mềm, KHÔNG ném ══════════════════════

    /// <summary>
    /// T9 — <c>EnsureWalletAsync</c> hỏng thật (không phải đua) trên đường reserve: phải hạ cấp thành
    /// <b>402 Insufficient</b> chứ không phải 500. Đây là đường tạo buổi thi của cả B2C lẫn B2B —
    /// hàng rào mềm đã có sẵn (reserve đọc ví thấy null ⇒ Insufficient), nên ném ở đây chỉ đổi một câu
    /// trả lời đúng sự thật lấy một stack trace.
    /// </summary>
    [Fact]
    public async Task Reserve_TaoViHong_HaCapThanh402_ChuKhongNem()
    {
        using var tdb = new PaymentTestDb();
        var fault = new ThrowOnceInterceptor("credit_accounts");

        await using var ctx = tdb.NewContext(null, [fault]);
        var result = await new CreditAccountService(ctx)
            .ReserveAsync(OwnerType.User, Guid.NewGuid(), Guid.NewGuid());

        Assert.True(fault.Fired, "Chưa ép được lỗi ghi ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(ReserveOutcome.Insufficient, result.Outcome);

        await using var db = tdb.NewContext();
        Assert.Empty(await db.CreditAccounts.ToListAsync());
        Assert.Empty(await db.CreditReservations.ToListAsync());
    }

    /// <summary>
    /// T10 — cùng bất đối xứng ở lệnh cấp quà: tạo ví hỏng ⇒ <c>WalletMissing</c> (câu trả lời nghiệp vụ
    /// có sẵn), không phải 500.
    /// </summary>
    [Fact]
    public async Task Grant_TaoViHong_TraWalletMissing_ChuKhongNem()
    {
        using var tdb = new PaymentTestDb();
        var fault = new ThrowOnceInterceptor("credit_accounts");

        await using var ctx = tdb.NewContext(null, [fault]);
        var result = await new AdminCreditService(ctx, new CreditAccountService(ctx))
            .GrantAsync(OwnerType.User, Guid.NewGuid(), 5, "quà", null, Admin);

        Assert.True(fault.Fired, "Chưa ép được lỗi ghi ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(GrantOutcome.WalletMissing, result.Outcome);

        await using var db = tdb.NewContext();
        Assert.Empty(await db.CreditAccounts.ToListAsync());
        Assert.Empty(await db.CreditTransactions.ToListAsync());
    }

    /// <summary>
    /// T11 — nhánh idempotency của lệnh cấp quà: hậu kiểm phải nhận ra request trùng và phát lại kết quả
    /// cũ. Bộ lọc CŨ (<c>PostgresException{SqlState:23505}</c>) khiến nhánh này KHÔNG BAO GIỜ chạy trên
    /// SQLite ⇒ 0% coverage cho đúng đường idempotency của tiền; ta chỉ biết nó "chắc là chạy".
    /// </summary>
    [Fact]
    public async Task Grant_DuaThatTrenKhoaIdempotency_PhatLaiKetQuaCu_KhongCongLanHai()
    {
        using var tdb = new PaymentTestDb();
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, owner, remaining: 1);
        await SeedLedgerAsync(tdb, owner, delta: 1, CreditTransactionReason.Purchase);
        const string key = "khoa-idem-1";

        // Đối thủ cấp xong quà cùng khoá (cộng ví + ghi sổ) ngay sau khi ta đọc ví — tức sau khi đường
        // tắt idempotency ở đầu hàm đã đọc và không thấy gì.
        //
        // Chen ở câu ĐỌC ví vì nó nằm NGOÀI transaction: dòng của đối thủ phải sống sót qua cú rollback
        // của ta, đúng như trên Postgres nơi hai admin là hai kết nối khác nhau. Chen vào câu UPDATE bên
        // trong transaction thì rollback cuốn luôn dòng đó đi ⇒ hậu kiểm không thấy gì ⇒ test sẽ "chứng
        // minh" ngược lại đúng điều nó sinh ra để khoá.
        await using var rival = await NewRivalAsync(tdb);
        var race = new RaceOnceInterceptor(
            sql => sql.Contains("credit_accounts", StringComparison.OrdinalIgnoreCase)
                   && sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase),
            cmd => RaceAsync(rival, cmd, db =>
            {
                db.CreditAccounts
                    .Where(a => a.OwnerType == OwnerType.User && a.OwnerId == owner)
                    .ExecuteUpdate(s => s.SetProperty(a => a.RemainingCredits, 6));
                db.CreditTransactions.Add(RivalLedger(owner, delta: 5, CreditTransactionReason.PromoGrant,
                    idempotencyKey: key, remainingAfter: 6));
            }));

        await using var ctx = tdb.NewContext(null, [race]);
        var result = await new AdminCreditService(ctx, new CreditAccountService(ctx))
            .GrantAsync(OwnerType.User, owner, 5, "quà", key, Admin);

        Assert.True(race.Fired, "Đối thủ chưa chen được ⇒ phép thử không chứng minh được gì.");
        Assert.Equal(GrantOutcome.Granted, result.Outcome);

        await using var db = tdb.NewContext();
        // Đúng MỘT bút toán quà: bên thua rollback rồi phát lại kết quả của bên thắng.
        Assert.Equal(1, await db.CreditTransactions
            .CountAsync(t => t.OwnerId == owner && t.Reason == CreditTransactionReason.PromoGrant));
    }

    // ══ Guard cấu trúc — cho mọi site về sau ═════════════════════════════════════════════════════

    /// <summary>
    /// T6 — các test hành vi ở trên chỉ phủ những site đang có. Guard này đọc thẳng mã nguồn: mọi
    /// <c>catch</c> bắt <c>DbUpdateException</c> phải có ít nhất một đường thoát — <c>throw;</c> (đẩy
    /// lên caller, và để execution strategy còn thấy lỗi tạm thời) hoặc <c>LogError</c> (hạ cấp mềm
    /// nhưng có dấu vết). Không có nó, người viết site thứ tám lại nuốt trọn một lần ghi mất tích.
    /// </summary>
    [Fact]
    public void MoiCatchDbUpdateException_DeuCoDuongThoat()
    {
        var offenders = CatchSwallowScanner.FindSilentSwallows(PaymentServiceDir());

        Assert.True(offenders.Count == 0,
            "catch (DbUpdateException) nuốt lỗi mà không throw; cũng không LogError:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// ĐỐI CHỨNG DƯƠNG cho guard trên. Một luật quét mã nguồn có thể "sạch" chỉ vì nó ĐÃ CHẾT (regex
    /// trượt, thư mục sai, cách nhận khối sai) — lúc đó nó im lặng đúng bằng lúc code sạch. Test này ép
    /// scanner nhìn ba site: một nuốt trọn (phải bắt), một <c>throw;</c> và một <c>LogError</c>
    /// (không được báo nhầm), cộng dạng khai báo <c>when (ex is DbUpdateException …)</c>.
    /// </summary>
    [Fact]
    public void ScannerNuotLoi_BatSiteNuotTron_BoQuaSiteCoDuongThoat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s11-catch-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "Offender.cs"),
            [
                "class Offender {",
                "    async Task X() {",
                "        try { await Y(); }",
                "        catch (DbUpdateException)",
                "        {",
                "            _db.Entry(z).State = EntityState.Detached;",
                "        }",
                "    }",
                "}",
            ]);
            File.WriteAllLines(Path.Combine(dir, "Rethrows.cs"),
            [
                "class Rethrows {",
                "    async Task X() {",
                "        try { await Y(); }",
                "        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)",
                "        {",
                "            _db.ChangeTracker.Clear();",
                "            if (await Missing()) throw;",
                "        }",
                "    }",
                "}",
            ]);
            File.WriteAllLines(Path.Combine(dir, "SoftDegrades.cs"),
            [
                "class SoftDegrades {",
                "    async Task X() {",
                "        try { await Y(); }",
                "        catch (DbUpdateException ex)",
                "        {",
                "            _logger?.LogError(ex, \"hạ cấp mềm\");",
                "        }",
                "    }",
                "}",
            ]);

            var found = CatchSwallowScanner.FindSilentSwallows(dir);

            Assert.Single(found);
            Assert.Contains("Offender.cs", found[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// T7 — S11-SAVEPOINT: cố ý KHÔNG viết savepoint tay, vì EF đã tự tạo savepoint quanh mỗi
    /// <c>SaveChanges</c> khi có transaction đang mở và tự cuộn về đó khi lỗi. Nhưng điều đó biến một cơ
    /// chế NGẦM thành ĐIỀU KIỆN ĐÚNG ĐẮN của bản vá này: hậu kiểm ở site Reserve/Refund chạy TRONG
    /// transaction còn sống, và nó chỉ đọc được nếu <c>SaveChanges</c> hỏng đã được cuộn về savepoint
    /// thay vì làm hỏng cả transaction. Hai thứ có thể phá ngầm giả định đó:
    ///
    /// <list type="bullet">
    /// <item><c>AutoSavepointsEnabled = false</c> — tắt hẳn cơ chế;</item>
    /// <item>mở transaction ở mức cô lập khác READ COMMITTED — hậu kiểm sẽ đọc ảnh chụp cũ và KHÔNG
    /// BAO GIỜ thấy dòng của đối thủ ⇒ mọi cuộc đua thành "lỗi thật" ⇒ 500 hàng loạt, mà không test
    /// nào trên SQLite đỏ.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void KhongAiTatAutoSavepoint_VaKhongAiDoiMucCoLap()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(PaymentServiceDir(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*")) continue;

                if (lines[i].Contains("AutoSavepointsEnabled"))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} tắt/đổi auto-savepoint: {trimmed}");

                // Cấm THẲNG chuỗi `IsolationLevel` thay vì đòi nó xuất hiện cùng dòng với
                // `BeginTransactionAsync(`. Bản đầu của guard này viết theo kiểu thứ hai và mutation
                // M11 lách qua một cách tầm thường: xuống dòng giữa lời gọi (đúng cách trình bày tự
                // nhiên cho một lời gọi dài) là hai vế nằm ở hai dòng ⇒ guard mù. Toàn service hiện
                // KHÔNG dùng `IsolationLevel` ở đâu cả, nên lệnh cấm thẳng vừa chặt hơn vừa không thể
                // bị phá bằng cách trình bày lại code.
                if (lines[i].Contains("IsolationLevel"))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} đổi mức cô lập: {trimmed}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Hậu kiểm sau catch dựa vào auto-savepoint + READ COMMITTED:\n  " + string.Join("\n  ", offenders));
    }

    // ══ helpers ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Bất biến tiền của repo, kèm SỐ DÒNG mong đợi — nhân đôi bút toán vẫn giữ tổng đúng ở
    /// một số ca, nên phải kiểm cả hai (mẫu <c>ExecutionStrategyDb25bTests</c>).</summary>
    private static async Task AssertLedgerBalancedAsync(PaymentDbContext db, Guid ownerId, int expectedRows)
    {
        var rows = await db.CreditTransactions.AsNoTracking().Where(t => t.OwnerId == ownerId).ToListAsync();
        var acc = await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == ownerId);

        Assert.Equal(expectedRows, rows.Count);
        Assert.Equal(rows.Sum(t => t.Delta), acc.RemainingCredits + acc.ReservedCredits);
    }

    // ── "dòng của đối thủ": ghi bằng MỘT DbContext THỨ HAI trên cùng connection ──────────────────
    //
    // ⚠ CỐ Ý KHÔNG dùng SQL thô như RaceBeforeOrderFlipInterceptor (RefundServiceF18Tests). Mẫu đó ghi
    // Guid bằng `guid.ToString()` — chữ THƯỜNG — trong khi EF lưu Guid vào SQLite dưới dạng TEXT chữ
    // HOA ('2BB6FBF1-…', đã đo). So sánh TEXT của SQLite phân biệt hoa/thường ⇒ dòng ghi bằng SQL thô
    // KHÔNG khớp FK, KHÔNG đụng UNIQUE, và KHÔNG được câu WHERE nào của EF nhìn thấy — nó cứ lặng lẽ
    // nằm đó. Hệ quả: cuộc đua ta tưởng đã dựng thì không hề xảy ra, ràng buộc ta tưởng đã kiểm thì
    // không hề nổ, và test vẫn "chạy xong". Mẫu cũ thoát nạn chỉ vì nó ghi vào một cột nullable rồi
    // ĐỌC lại (Guid parse không phân biệt hoa thường), không bao giờ SO SÁNH hay JOIN trên giá trị đó.
    //
    // Đi qua EF thì mọi ánh xạ kiểu (Guid, enum→string, DateTime, cột mặc định) do chính EF lo — hết
    // chỗ để đoán sai.
    /// <summary>
    /// Context của "đối thủ", đã ĐƯỢC KHỞI TẠO SẴN.
    ///
    /// <para>Phải warm-up bằng một truy vấn thật, không chỉ <c>new</c>: EF dựng connection theo kiểu
    /// LƯỜI, và lúc dựng nó gọi <c>SqliteConnection.CreateFunction</c> — thao tác bị SQLite từ chối khi
    /// connection đang có statement chạy (Error 5: "unable to delete/modify user-function due to active
    /// statements"). Mà thời điểm đối thủ ra tay lại nằm đúng trong <c>ReaderExecutedAsync</c>, tức lúc
    /// một reader đang mở. Nên phần khởi tạo phải xong TRƯỚC khi cuộc đua bắt đầu.</para>
    /// </summary>
    private static async Task<PaymentDbContext> NewRivalAsync(PaymentTestDb tdb)
    {
        var rival = tdb.NewContext();
        await rival.CreditAccounts.AnyAsync();
        return rival;
    }

    private static async Task RaceAsync(PaymentDbContext rival, DbCommand origin, Action<PaymentDbContext> stage)
    {
        // Cùng connection: nếu bên bị đua đang mở transaction thì phải nhập cuộc, nếu không SQLite từ
        // chối lệnh. Ngoài transaction thì dòng của đối thủ tự commit — và đó mới là điều test cần ở
        // những ca mà bên bị đua sẽ rollback (dòng đối thủ PHẢI sống sót qua cú rollback đó).
        if (origin.Transaction is not null)
            await rival.Database.UseTransactionAsync(origin.Transaction);

        stage(rival);
        await rival.SaveChangesAsync();
        rival.ChangeTracker.Clear();
    }

    private static CreditAccount RivalWallet(Guid ownerId) => new()
    {
        Id = Guid.NewGuid(),
        OwnerType = OwnerType.User,
        OwnerId = ownerId,
        PaymentMode = PaymentMode.Prepaid,
        Status = CreditAccountStatus.Active,
        RemainingCredits = 0,
        ReservedCredits = 0,
        UpdatedAt = DateTime.UtcNow,
    };

    private static CreditReservation RivalReservation(Guid ownerId, Guid sessionId) => new()
    {
        Id = Guid.NewGuid(),
        OwnerType = OwnerType.User,
        OwnerId = ownerId,
        SessionId = sessionId,
        Status = ReservationStatus.Reserved,
        FundedBy = ReservationFunding.Credit,
        PaymentMode = PaymentMode.Prepaid,
        CreatedAt = DateTime.UtcNow,
    };

    private static CreditTransaction RivalLedger(
        Guid ownerId, int delta, CreditTransactionReason reason,
        Guid? reverses = null, string? idempotencyKey = null, int? remainingAfter = null) => new()
    {
        Id = Guid.NewGuid(),
        OwnerType = OwnerType.User,
        OwnerId = ownerId,
        Delta = delta,
        Reason = reason,
        ReversesTransactionId = reverses,
        GrantIdempotencyKey = idempotencyKey,
        GrantRemainingCreditsAfter = remainingAfter,
        CreatedAt = DateTime.UtcNow,
    };

    // ── seed ────────────────────────────────────────────────────────────────────────────────────

    private static async Task SeedWalletAsync(PaymentTestDb tdb, Guid ownerId, int remaining)
    {
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = 0,
            UpdatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();
    }

    private static async Task SeedLedgerAsync(
        PaymentTestDb tdb, Guid ownerId, int delta, CreditTransactionReason reason,
        Guid? orderId = null, Guid? reverses = null)
    {
        var tx = new CreditTransaction
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            OrderId = orderId,
            Delta = delta,
            Reason = reason,
            ReversesTransactionId = reverses,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        tdb.Db.CreditTransactions.Add(tx);
        await tdb.Db.SaveChangesAsync();
    }

    private static async Task<(Guid OrderId, Guid PurchaseId)> SeedOrderWithPurchaseAsync(
        PaymentTestDb tdb, Guid ownerId, int credits, OrderStatus status)
    {
        var orderId = Guid.NewGuid();
        tdb.Db.Orders.Add(new Order
        {
            Id = orderId,
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            Kind = OrderKind.CreditPack,
            Status = status,
            AmountVnd = 100_000,
            PayosOrderCode = Random.Shared.NextInt64(100_000_000_000, 999_999_999_999),
            PaidAt = DateTime.UtcNow.AddMinutes(-2),
            CreatedAt = DateTime.UtcNow.AddMinutes(-3),
        });
        var purchase = new CreditTransaction
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            OrderId = orderId,
            Delta = credits,
            Reason = CreditTransactionReason.Purchase,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        };
        tdb.Db.CreditTransactions.Add(purchase);
        await tdb.Db.SaveChangesAsync();
        return (orderId, purchase.Id);
    }

    private static async Task<(Guid OrderId, Guid OwnerId, Guid PurchaseId)> SeedPaidOrderAsync(
        PaymentTestDb tdb, int credits, int remaining)
    {
        var owner = Guid.NewGuid();
        await SeedWalletAsync(tdb, owner, remaining);
        var (orderId, purchaseId) = await SeedOrderWithPurchaseAsync(tdb, owner, credits, OrderStatus.Paid);
        return (orderId, owner, purchaseId);
    }

    /// <summary>Đơn + gói; ví CHƯA tồn tại (đúng đường "lần mua đầu").</summary>
    private static async Task<Guid> SeedPendingOrderAsync(
        PaymentTestDb tdb, long orderCode, int credits,
        OrderKind kind = OrderKind.CreditPack, OrderStatus status = OrderStatus.Pending)
    {
        var owner = Guid.NewGuid();
        var pkg = new ProductPackage
        {
            Id = Guid.NewGuid(),
            Name = $"Pack {credits}",
            Type = kind == OrderKind.CreditPack ? PackageType.OneTime : PackageType.Subscription,
            PriceVnd = 100_000,
            InterviewCredits = credits,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        tdb.Db.ProductPackages.Add(pkg);
        tdb.Db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = owner,
            Kind = kind,
            PackageId = pkg.Id,
            Status = status,
            AmountVnd = 100_000,
            PayosOrderCode = orderCode,
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            CreatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();
        return owner;
    }

    /// <summary>Neo theo đường dẫn file NGUỒN lúc biên dịch — KHÔNG dò ngược tìm thư mục <c>.git</c>:
    /// trong git worktree thì <c>.git</c> là một FILE chứ không phải thư mục, và cách dò đó chết câm
    /// (mẫu <c>ExecutionStrategyDb25bTests.RepoRoot</c>).</summary>
    private static string PaymentServiceDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(here)!, "..", "Isas.PaymentService"));
}
