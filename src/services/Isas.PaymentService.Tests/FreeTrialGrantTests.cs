using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// F7 — suất dùng thử 3 lượt cho user B2C mới.
///
/// Trước F7: ví chỉ tạo lazy ở webhook Paid ⇒ user vừa đăng ký tạo buổi luyện đầu tiên luôn nhận 402,
/// cả phễu B2C bắt trả tiền trước khi được thử. Nay ví được tạo kèm suất dùng thử ở lần reserve đầu
/// (và ở webhook, cho người mua trước khi thi).
///
/// Thiết kế cần khoá lại bằng test, vì đây đều là chỗ dễ "sửa hộ" thành bug tiền:
///  · credit tặng nằm CHUNG `remaining_credits` + có bút toán `FreeGrant` ⇒ bất biến
///    `remaining + reserved = Σ delta` vẫn đúng, drift vẫn dò được (khác hẳn "xô free không sổ sách").
///  · cấp NGAY TRONG câu INSERT tạo ví ⇒ không có đường nào cấp lần hai.
///  · chỉ owner_type=User; Org giữ nguyên no-wallet → 402.
/// </summary>
public class FreeTrialGrantTests
{
    private static CreditAccountService NewService(PaymentTestDb tdb, int freeTrialCredits = 3) =>
        new(tdb.NewContext(), logger: null,
            billing: Options.Create(new BillingSettings { FreeTrialCredits = freeTrialCredits }));

    // Trần dùng thử là THẬT: 3 lượt đầu chạy, lượt thứ 4 → 402 (ví cạn, không có gì bù).
    [Fact]
    public async Task BaLuotDau_OK_LuotThu4_Insufficient()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();

        for (var i = 1; i <= 3; i++)
        {
            var ok = await NewService(tdb).ReserveAsync(OwnerType.User, userId, Guid.NewGuid());
            Assert.Equal(ReserveOutcome.Reserved, ok.Outcome);
        }

        var fourthSession = Guid.NewGuid();
        var fourth = await NewService(tdb).ReserveAsync(OwnerType.User, userId, fourthSession);

        Assert.Equal(ReserveOutcome.Insufficient, fourth.Outcome);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(0, acc.RemainingCredits);
        Assert.Equal(3, acc.ReservedCredits);
        Assert.Equal(3, acc.FreeCreditsGranted);
        // PAY-5 — lượt bị từ chối KHÔNG để lại reservation mồ côi.
        Assert.Equal(0, await read.CreditReservations.CountAsync(r => r.SessionId == fourthSession));
    }

    // Ví đã tồn tại (do webhook mua credit tạo) → reserve KHÔNG bao giờ top-up thêm suất dùng thử.
    // Đây là đường "credit tặng vô hạn" kinh điển nếu ai đó thêm nhánh "granted==0 thì cấp".
    [Fact]
    public async Task ViDaTonTai_ReserveKhongTopUp()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        tdb.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = userId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 1,
            ReservedCredits = 0,
            FreeCreditsGranted = 0, // ví cũ, có trước F7
            UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        await NewService(tdb).ReserveAsync(OwnerType.User, userId, Guid.NewGuid());

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(0, acc.FreeCreditsGranted);   // KHÔNG được tặng hồi tố
        Assert.Equal(0, acc.RemainingCredits);     // 1 − 1, không phải 1 + 3 − 1
        Assert.Equal(0, await read.CreditTransactions.CountAsync(
            t => t.OwnerId == userId && t.Reason == CreditTransactionReason.FreeGrant));
    }

    // Hai reserve ĐỒNG THỜI của một user mới toanh (2 session khác nhau, 2 DbContext khác nhau) →
    // vẫn đúng MỘT ví, tặng đúng MỘT lần. Nếu phần cấp bị tách khỏi câu INSERT thành một UPDATE
    // tiếp sau, bên thua race sẽ cấp lần hai và granted thành 6.
    [Fact]
    public async Task HaiReserveDongThoi_ChiTangMotLan()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();

        var a = NewService(tdb).ReserveAsync(OwnerType.User, userId, Guid.NewGuid());
        var b = NewService(tdb).ReserveAsync(OwnerType.User, userId, Guid.NewGuid());
        await Task.WhenAll(a, b);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(3, acc.FreeCreditsGranted);
        Assert.Equal(1, await read.CreditTransactions.CountAsync(
            t => t.OwnerId == userId && t.Reason == CreditTransactionReason.FreeGrant));
        Assert.Equal(1, await read.CreditAccounts.CountAsync(x => x.OwnerId == userId));
    }

    // Kill-switch: Billing:FreeTrialCredits = 0 → hành vi về đúng như trước F7 (không ví, không sổ, 402).
    [Fact]
    public async Task TatCauHinh_KhongTangGi_VaVanInsufficient()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();

        var result = await NewService(tdb, freeTrialCredits: 0)
            .ReserveAsync(OwnerType.User, userId, Guid.NewGuid());

        Assert.Equal(ReserveOutcome.Insufficient, result.Outcome);
        using var read = tdb.NewContext();
        Assert.Equal(0, await read.CreditAccounts.CountAsync(a => a.OwnerId == userId));
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.OwnerId == userId));
    }

    // Ví Org tạo mới (đường webhook mua credit org) KHÔNG kèm suất dùng thử — B2B đi ví Org (BC-1).
    [Fact]
    public async Task TaoViOrg_KhongCoSuatDungThu()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();

        await NewService(tdb).CreateAccountAsync(OwnerType.Org, orgId);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == orgId);
        Assert.Equal(0, acc.FreeCreditsGranted);
        Assert.Equal(0, acc.RemainingCredits);
        Assert.Equal(0, await read.CreditTransactions.CountAsync(t => t.OwnerId == orgId));
    }

    // Credit tặng tiêu y hệt credit mua: Consume ghi sổ −1 và KHÔNG hoàn lại remaining.
    // Khoá việc "ConsumeAsync/ReleaseAsync không đổi một dòng nào" — nếu ai đó thêm nhánh xử lý riêng
    // cho credit tặng thì đây là chỗ vỡ.
    [Fact]
    public async Task CreditTang_Consume_GiongHetCreditMua()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        await NewService(tdb).ReserveAsync(OwnerType.User, userId, sessionId);
        await NewService(tdb).ConsumeAsync(sessionId);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(2, acc.RemainingCredits);
        Assert.Equal(0, acc.ReservedCredits);

        var ledger = await read.CreditTransactions.Where(t => t.OwnerId == userId).ToListAsync();
        Assert.Contains(ledger, t => t.Reason == CreditTransactionReason.Consume && t.Delta == -1);
        Assert.Equal(ledger.Sum(t => t.Delta), acc.RemainingCredits + acc.ReservedCredits);
    }

    // Credit tặng cũng được HOÀN như credit mua khi buổi bị bỏ (PAY-13: cả buổi lỗi chấm → release).
    [Fact]
    public async Task CreditTang_Release_HoanVeRemaining()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        await NewService(tdb).ReserveAsync(OwnerType.User, userId, sessionId);
        await NewService(tdb).ReleaseAsync(sessionId);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(3, acc.RemainingCredits);  // hoàn nguyên vẹn
        Assert.Equal(0, acc.ReservedCredits);
        Assert.Equal(3, acc.FreeCreditsGranted);

        var ledger = await read.CreditTransactions.Where(t => t.OwnerId == userId).ToListAsync();
        Assert.Equal(ledger.Sum(t => t.Delta), acc.RemainingCredits + acc.ReservedCredits);
    }

    // Idempotency PAY-4 vẫn nguyên: gọi lại cùng session → chỉ trừ 1 credit tặng, không tặng lại.
    [Fact]
    public async Task GoiLaiCungSession_ChiTruMotLan()
    {
        using var tdb = new PaymentTestDb();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var first = await NewService(tdb).ReserveAsync(OwnerType.User, userId, sessionId);
        var second = await NewService(tdb).ReserveAsync(OwnerType.User, userId, sessionId);

        Assert.Equal(ReserveOutcome.Reserved, first.Outcome);
        Assert.Equal(ReserveOutcome.AlreadyReserved, second.Outcome);

        using var read = tdb.NewContext();
        var acc = await read.CreditAccounts.SingleAsync(a => a.OwnerId == userId);
        Assert.Equal(2, acc.RemainingCredits);
        Assert.Equal(1, acc.ReservedCredits);
        Assert.Equal(3, acc.FreeCreditsGranted);
        Assert.Equal(1, await read.CreditReservations.CountAsync(r => r.SessionId == sessionId));
    }
}
