using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

// P4 — POST /internal/credits/reserve. Verify: (a) reserve OK → reserved+1/remaining-1;
// (b) hết credit → Insufficient (402) + KHÔNG có reservation dư; (c) idempotent theo session_id.
public class CreditReserveServiceTests
{
    private static async Task<CreditAccount> SeedAccountAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining,
        CreditAccountStatus status = CreditAccountStatus.Active)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = status,
            RemainingCredits = remaining,
            ReservedCredits = 0,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(acc);
        await tdb.Db.SaveChangesAsync();
        return acc;
    }

    // Ví postpaid (chỉ Org): remaining=0 (không dùng), dồn nợ tới credit_limit; period_usage = nợ kỳ đã tiêu.
    private static async Task<CreditAccount> SeedPostpaidAccountAsync(
        PaymentTestDb tdb, Guid orgId, int? creditLimit, int periodUsage = 0, int reserved = 0,
        CreditAccountStatus status = CreditAccountStatus.Active)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = PaymentMode.Postpaid,
            Status = status,
            RemainingCredits = 0,          // postpaid KHÔNG dùng remaining
            ReservedCredits = reserved,
            CreditLimit = creditLimit,
            PeriodUsage = periodUsage,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(acc);
        await tdb.Db.SaveChangesAsync();
        return acc;
    }

    // (a) reserve thành công → reserved+1, remaining-1, đúng 1 reservation Reserved.
    [Fact]
    public async Task Reserve_ConCredit_GiuCho_TruRemaining()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);

        Assert.Equal(ReserveOutcome.Reserved, result.Outcome);
        Assert.NotNull(result.ReservationId);
        Assert.Equal(1, result.ReservedCredits);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(4, acc.RemainingCredits);   // remaining -1
        Assert.Equal(1, acc.ReservedCredits);    // reserved +1

        var reservation = await read.CreditReservations.SingleAsync(r => r.SessionId == sessionId);
        Assert.Equal(ReservationStatus.Reserved, reservation.Status);
        Assert.Equal(userId, reservation.OwnerId);
    }

    // (b) hết credit (remaining=0) → Insufficient (controller→402) + KHÔNG để lại reservation, số dư nguyên.
    [Fact]
    public async Task Reserve_HetCredit_Insufficient_KhongCoReservation()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 0);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.OutOfCredit, result.Outcome);
        Assert.Null(result.ReservationId);

        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(0, acc.RemainingCredits);
        Assert.Equal(0, acc.ReservedCredits);
    }

    // F7 — ví ORG chưa tồn tại → vẫn Insufficient (block, không tạo session) + không đẻ reservation
    // + KHÔNG tự tạo ví. Suất dùng thử là chuyện của B2C; ví Org do OrgAdmin mua credit tạo ra (BC-1).
    // Đây là vế GIỮ NGUYÊN của hành vi no-wallet cũ, sau khi F7 đổi vế User.
    [Fact]
    public async Task Reserve_ViOrgChuaTonTai_Insufficient_KhongTuTaoVi()
    {
        using var tdb = new PaymentTestDb();
        var sessionId = Guid.NewGuid();
        var orgId = Guid.NewGuid();

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.NoWallet, result.Outcome);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
        Assert.Equal(0, await read.CreditAccounts.CountAsync(a => a.OwnerId == orgId));
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.OwnerId == orgId));
    }

    // F7 — ví USER chưa tồn tại → KHÔNG còn 402 nữa: tạo ví kèm suất dùng thử rồi reserve bình thường.
    // Đây chính là ca "user vừa đăng ký, tạo buổi luyện đầu tiên" mà trước F7 luôn nhận 402.
    [Fact]
    public async Task Reserve_ViUserChuaTonTai_TaoViKemSuatDungThu_RoiReserve()
    {
        using var tdb = new PaymentTestDb();
        var sessionId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);

        Assert.Equal(ReserveOutcome.Reserved, result.Outcome);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(3, acc.FreeCreditsGranted);
        Assert.Equal(2, acc.RemainingCredits);  // 3 tặng − 1 vừa giữ chỗ
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(1, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));

        // Ghi sổ đúng 1 bút toán FreeGrant +3 (không gắn order/session) → bất biến số dư vẫn kiểm được.
        var ledger = await read.CreditTransactions.Where(t => t.OwnerId == userId).ToListAsync();
        var grant = Assert.Single(ledger);
        Assert.Equal(CreditTransactionReason.FreeGrant, grant.Reason);
        Assert.Equal(3, grant.Delta);
        Assert.Null(grant.OrderId);
        Assert.Null(grant.SessionId);

        // Bất biến sổ cái: remaining + reserved == Σ delta.
        Assert.Equal(ledger.Sum(t => t.Delta), acc.RemainingCredits + acc.ReservedCredits);
    }

    // (c) idempotent theo session_id (PAY-4): gọi 2 lần cùng session → đúng 1 reservation, chỉ trừ 1 lần.
    [Fact]
    public async Task Reserve_GoiLaiCungSession_Idempotent()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);

        var first = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);
        var second = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.User, userId, sessionId);

        Assert.Equal(ReserveOutcome.Reserved, first.Outcome);
        Assert.Equal(ReserveOutcome.AlreadyReserved, second.Outcome);
        Assert.Equal(first.ReservationId, second.ReservationId);   // cùng reservation
        Assert.Equal(1, second.ReservedCredits);

        using var read = tdb.NewContext();
        Assert.Equal(1, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId)); // đúng 1
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(4, acc.RemainingCredits);   // chỉ trừ 1 lần
        Assert.Equal(1, acc.ReservedCredits);
    }

    // Account Suspended → chặn reserve mới (payment.md §State machine) dù còn remaining.
    [Fact]
    public async Task Reserve_AccountSuspended_Insufficient()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 3, status: CreditAccountStatus.Suspended);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.Suspended, result.Outcome);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(3, acc.RemainingCredits); // không đụng số dư
    }

    // Hai session khác nhau trên cùng ví → trừ độc lập (2 reservation, remaining -2).
    [Fact]
    public async Task Reserve_HaiSessionKhacNhau_TruDocLap()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.User, userId, remaining: 5);
        var svc = new CreditAccountService(tdb.NewContext());

        var s1 = await svc.ReserveAsync(OwnerType.User, userId, Guid.NewGuid());
        var s2 = await svc.ReserveAsync(OwnerType.User, userId, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, s1.Outcome);
        Assert.Equal(ReserveOutcome.Reserved, s2.Outcome);
        Assert.NotEqual(s1.ReservationId, s2.ReservationId);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(3, acc.RemainingCredits);
        Assert.Equal(2, acc.ReservedCredits);
    }

    // ── P8a — Postpaid: hạn mức + dồn nợ (payment.md §Kế toán POSTPAID) ──────────────────────────

    // (a) postpaid org reserve tới sát credit_limit → OK; mỗi reserve tăng reserved (KHÔNG trừ remaining).
    //     period_usage KHÔNG đổi khi reserve (doc: "Consume mới cộng period_usage, không phải reserve").
    [Fact]
    public async Task Reserve_Postpaid_ToiHanMuc_OK_TangReserved()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: 3);
        var svc = new CreditAccountService(tdb.NewContext());

        var r1 = await svc.ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid());
        var r2 = await svc.ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid());
        var r3 = await svc.ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid()); // reserve thứ 3 = credit_limit

        Assert.Equal(ReserveOutcome.Reserved, r1.Outcome);
        Assert.Equal(ReserveOutcome.Reserved, r2.Outcome);
        Assert.Equal(ReserveOutcome.Reserved, r3.Outcome);
        Assert.Equal(3, r3.ReservedCredits);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(3, acc.ReservedCredits);   // dồn tới hạn mức
        Assert.Equal(0, acc.RemainingCredits);  // postpaid KHÔNG trừ remaining
        Assert.Equal(0, acc.PeriodUsage);       // reserve KHÔNG cộng nợ kỳ (chỉ Consume mới cộng)
        Assert.Equal(3, await read.CreditReservations.CountAsync(r => r.OwnerId == orgId));
    }

    // (b) reserve vượt credit_limit → Insufficient (controller→402) + KHÔNG để lại reservation (no orphan).
    [Fact]
    public async Task Reserve_Postpaid_VuotHanMuc_Insufficient_KhongOrphan()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: 2);
        var svc = new CreditAccountService(tdb.NewContext());

        await svc.ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid()); // reserved 1
        await svc.ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid()); // reserved 2 = credit_limit

        var overSession = Guid.NewGuid();
        var over = await svc.ReserveAsync(OwnerType.Org, orgId, overSession); // 0+2+1=3 > 2 → 402

        Assert.Equal(ReserveOutcome.LimitReached, over.Outcome);
        Assert.Null(over.ReservationId);

        using var read = tdb.NewContext();
        // session vượt hạn mức KHÔNG được để lại reservation dư (no orphan)
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == overSession));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(2, acc.ReservedCredits);   // giữ nguyên ở hạn mức, không tăng
        Assert.Equal(0, acc.RemainingCredits);
    }

    // period_usage (nợ kỳ đã tiêu) tính chung với reserved vào hạn mức: limit=3, đã dùng 2 → chỉ còn 1 chỗ.
    [Fact]
    public async Task Reserve_Postpaid_NoKyDaCong_TinhVaoHanMuc()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: 3, periodUsage: 2);
        var svc = new CreditAccountService(tdb.NewContext());

        var ok = await svc.ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid());   // 2+0+1=3 ≤ 3 → OK
        var over = await svc.ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid()); // 2+1+1=4 > 3 → 402

        Assert.Equal(ReserveOutcome.Reserved, ok.Outcome);
        Assert.Equal(ReserveOutcome.LimitReached, over.Outcome);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(2, acc.PeriodUsage);   // reserve KHÔNG đổi period_usage
    }

    // credit_limit chưa đặt (NULL) → không được reserve (postpaid cần admin đặt hạn mức) → Insufficient.
    [Fact]
    public async Task Reserve_Postpaid_ChuaDatHanMuc_Insufficient()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: null);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.LimitReached, result.Outcome);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(0, acc.ReservedCredits);
    }

    // Account postpaid Suspended → chặn reserve mới dù còn hạn mức (payment.md §State machine).
    [Fact]
    public async Task Reserve_Postpaid_Suspended_Insufficient()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: 5, status: CreditAccountStatus.Suspended);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.Suspended, result.Outcome);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(0, acc.ReservedCredits);
    }

    // (c-postpaid) idempotent theo session_id: gọi 2 lần cùng session → 1 reservation, reserved chỉ +1.
    [Fact]
    public async Task Reserve_Postpaid_GoiLaiCungSession_Idempotent()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: 5);

        var first = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);
        var second = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.Reserved, first.Outcome);
        Assert.Equal(ReserveOutcome.AlreadyReserved, second.Outcome);
        Assert.Equal(first.ReservationId, second.ReservationId);

        using var read = tdb.NewContext();
        Assert.Equal(1, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(1, acc.ReservedCredits);   // chỉ giữ 1 lần
    }

    // ── BK17 — Postpaid: hóa đơn Overdue chặn reserve mới (payment.md:379/431 · §State machine) ──────

    private static async Task SeedInvoiceAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, InvoiceStatus status)
    {
        tdb.Db.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PeriodStart = DateTime.UtcNow.AddDays(-30),
            PeriodEnd = DateTime.UtcNow,
            InterviewCount = 3,
            UnitPrice = 50_000,
            Amount = 150_000,
            Status = status,
            CreatedAt = DateTime.UtcNow.AddDays(-30)
        });
        await tdb.Db.SaveChangesAsync();
    }

    // Postpaid còn hóa đơn Overdue (nợ kỳ trước chưa trả) → reserve mới bị chặn (402) + no orphan.
    [Fact]
    public async Task Reserve_Postpaid_CoHoaDonOverdue_Insufficient_KhongOrphan()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: 5); // còn hạn mức
        await SeedInvoiceAsync(tdb, OwnerType.Org, orgId, InvoiceStatus.Overdue);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, sessionId);

        Assert.Equal(ReserveOutcome.InvoiceOverdue, result.Outcome);
        Assert.Null(result.ReservationId);

        using var read = tdb.NewContext();
        // no orphan: reservation vừa chèn đã rollback
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(0, acc.ReservedCredits);   // KHÔNG tăng reserved
    }

    // Postpaid có hóa đơn nhưng KHÔNG Overdue (Issued/Paid) → reserve vẫn OK (chỉ Overdue mới chặn).
    [Fact]
    public async Task Reserve_Postpaid_HoaDonKhongOverdue_KhongChan()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedPostpaidAccountAsync(tdb, orgId, creditLimit: 5);
        await SeedInvoiceAsync(tdb, OwnerType.Org, orgId, InvoiceStatus.Issued);
        await SeedInvoiceAsync(tdb, OwnerType.Org, orgId, InvoiceStatus.Paid);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, result.Outcome);
        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(1, acc.ReservedCredits);
    }

    // Overdue chỉ chặn nhánh POSTPAID: ví prepaid (dù cùng org có hóa đơn Overdue) reserve vẫn OK.
    [Fact]
    public async Task Reserve_Prepaid_CoHoaDonOverdue_KhongAnhHuong()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedAccountAsync(tdb, OwnerType.Org, orgId, remaining: 5); // prepaid
        await SeedInvoiceAsync(tdb, OwnerType.Org, orgId, InvoiceStatus.Overdue);

        var result = await new CreditAccountService(tdb.NewContext())
            .ReserveAsync(OwnerType.Org, orgId, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Reserved, result.Outcome); // prepaid KHÔNG kiểm Overdue
        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(4, acc.RemainingCredits);
        Assert.Equal(1, acc.ReservedCredits);
    }
}
