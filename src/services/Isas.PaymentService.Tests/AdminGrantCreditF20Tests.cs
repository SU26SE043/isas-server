using System.Security.Claims;
using Isas.PaymentService.Controllers;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// F20 (vế Payment) — PlatformAdmin cấp credit khuyến mãi + đọc ví người khác. Hai vế Auth (cấm tài
/// khoản · đặt lại mật khẩu hộ) đã làm ở vòng trước.
///
/// Trước vòng này KHÔNG có đường nào để admin chạm tới ví người khác: <c>me/account</c> suy chủ ví từ
/// JWT nên chỉ bao giờ nói về chính người gọi.
/// </summary>
public class AdminGrantCreditF20Tests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private static AdminCreditService NewService(PaymentTestDb tdb, int freeTrial = 3) =>
        new(tdb.Db, new CreditAccountService(
            tdb.Db, null, Options.Create(new BillingSettings { FreeTrialCredits = freeTrial })));

    private static AdminCreditsController NewController(PaymentTestDb tdb, params Claim[] claims) =>
        new(tdb.Db, NewService(tdb))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                },
            },
        };

    private static async Task<CreditAccount> SeedWalletAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId, int remaining,
        int reserved = 0, CreditAccountStatus status = CreditAccountStatus.Active)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = ownerType,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = status,
            RemainingCredits = remaining,
            ReservedCredits = reserved,
            UpdatedAt = DateTime.UtcNow,
        };
        tdb.Db.CreditAccounts.Add(acc);
        if (remaining + reserved > 0)
            tdb.Db.CreditTransactions.Add(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                OwnerType = ownerType,
                OwnerId = ownerId,
                Delta = remaining + reserved,
                Reason = CreditTransactionReason.Purchase,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            });
        await tdb.Db.SaveChangesAsync();
        return acc;
    }

    /// <summary>Bất biến sổ cái: <c>remaining + reserved = Σ delta</c>.</summary>
    private static async Task AssertLedgerInvariantAsync(
        PaymentTestDb tdb, OwnerType ownerType, Guid ownerId)
    {
        await using var db = tdb.NewContext();
        var acc = await db.CreditAccounts.AsNoTracking()
            .SingleAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId);
        var sum = await db.CreditTransactions
            .Where(t => t.OwnerType == ownerType && t.OwnerId == ownerId)
            .SumAsync(t => (int?)t.Delta) ?? 0;

        Assert.Equal(sum, acc.RemainingCredits + acc.ReservedCredits);
    }

    // ── cấp credit ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cap_ViTang_VaCoButToanGhiRoNguoiCap()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 2);

        var result = await NewService(tdb).GrantAsync(
            OwnerType.User, ownerId, 5, "đền bù sự cố chấm điểm", null, Admin);

        Assert.Equal(GrantOutcome.Granted, result.Outcome);
        Assert.Equal(5, result.CreditsGranted);
        Assert.Equal(7, result.RemainingCredits);

        await using var db = tdb.NewContext();
        var tx = await db.CreditTransactions.AsNoTracking()
            .SingleAsync(t => t.Reason == CreditTransactionReason.PromoGrant);

        Assert.Equal(5, tx.Delta);
        Assert.Equal(Admin, tx.GrantedBy);                       // ai cấp — cột chính của F20
        Assert.Equal("đền bù sự cố chấm điểm", tx.Note);
        Assert.Null(tx.OrderId);                                 // quà không phát sinh từ đơn nào
        Assert.Null(tx.SessionId);

        await AssertLedgerInvariantAsync(tdb, OwnerType.User, ownerId);
    }

    /// <summary>
    /// Quà mang nhãn RIÊNG (<c>PromoGrant</c>), không đội lốt <c>Purchase</c> (tiền thật) hay
    /// <c>FreeGrant</c> (suất dùng thử tự động). Ba nguồn credit = ba nhãn; nhập nhèm thì báo cáo doanh
    /// thu đọc sổ cái sẽ tưởng quà là tiền, còn phép "ví này đã dùng suất dùng thử chưa" thì hỏng.
    /// </summary>
    [Fact]
    public async Task Cap_ButToanMangNhanRieng_KhongDoiLotPurchaseHayFreeGrant()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 0);

        await NewService(tdb).GrantAsync(OwnerType.User, ownerId, 4, "khuyến mãi", null, Admin);

        await using var db = tdb.NewContext();
        var rows = await db.CreditTransactions.AsNoTracking()
            .Where(t => t.OwnerId == ownerId).ToListAsync();

        Assert.Single(rows, t => t.Reason == CreditTransactionReason.PromoGrant);
        Assert.DoesNotContain(rows, t => t.Reason == CreditTransactionReason.Purchase && t.Delta == 4);
        Assert.DoesNotContain(rows, t => t.Reason == CreditTransactionReason.FreeGrant);

        // Quà KHÔNG được tính vào suất dùng thử của ví.
        var acc = await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == ownerId);
        Assert.Equal(0, acc.FreeCreditsGranted);
    }

    /// <summary>
    /// ⚠ Bẫy nêu thẳng trong brief: quà KHÔNG được cộng thành doanh thu (F19). Bảo đảm theo CẤU TRÚC —
    /// báo cáo đọc <c>orders</c>, quà chỉ ghi <c>credit_transactions</c> và không sinh đơn nào. Test
    /// khoá điều đó lại với đúng nhãn mới <c>PromoGrant</c>.
    /// </summary>
    [Fact]
    public async Task Cap_KhongLamTangDoanhThu()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 0);

        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddDays(1);
        var truoc = await new RevenueService(tdb.Db).GetRevenueAsync(from, to, RevenueGranularity.Day);

        await NewService(tdb).GrantAsync(OwnerType.User, ownerId, 100, "quà to", null, Admin);

        var sau = await new RevenueService(tdb.Db).GetRevenueAsync(from, to, RevenueGranularity.Day);

        Assert.Equal(truoc.GrossRevenueVnd, sau.GrossRevenueVnd);
        Assert.Equal(0, sau.GrossRevenueVnd);
        Assert.Equal(0, sau.PaidOrderCount);
    }

    /// <summary>
    /// Ví chưa tồn tại → tạo qua <c>CreateAccountAsync</c>, tức là NƠI DUY NHẤT cấp suất dùng thử F7
    /// (PAY-14). Hệ quả có chủ ý: user mới được tặng quà thì ví sinh ra kèm CẢ suất dùng thử. Tự INSERT
    /// ví ở đây sẽ im lặng tước mất 3 credit dùng thử của đúng người vừa được tặng quà.
    /// </summary>
    [Fact]
    public async Task Cap_ChoViChuaTonTai_TaoViQuaDuongF7_VanCoSuatDungThu()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();

        var result = await NewService(tdb, freeTrial: 3).GrantAsync(
            OwnerType.User, ownerId, 5, "quà", null, Admin);

        Assert.Equal(GrantOutcome.Granted, result.Outcome);
        Assert.Equal(8, result.RemainingCredits);   // 3 dùng thử + 5 quà

        await using var db = tdb.NewContext();
        var acc = await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == ownerId);
        Assert.Equal(3, acc.FreeCreditsGranted);

        var reasons = await db.CreditTransactions.AsNoTracking()
            .Where(t => t.OwnerId == ownerId).Select(t => t.Reason).ToListAsync();
        Assert.Contains(CreditTransactionReason.FreeGrant, reasons);
        Assert.Contains(CreditTransactionReason.PromoGrant, reasons);

        await AssertLedgerInvariantAsync(tdb, OwnerType.User, ownerId);
    }

    /// <summary>Ví Org không có suất dùng thử (BC-1) — quà vẫn cấp được bình thường.</summary>
    [Fact]
    public async Task Cap_ChoViOrgChuaTonTai_KhongCoSuatDungThu()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();

        var result = await NewService(tdb).GrantAsync(OwnerType.Org, orgId, 20, "quà B2B", null, Admin);

        Assert.Equal(GrantOutcome.Granted, result.Outcome);
        Assert.Equal(20, result.RemainingCredits);

        await using var db = tdb.NewContext();
        var acc = await db.CreditAccounts.AsNoTracking()
            .SingleAsync(a => a.OwnerType == OwnerType.Org && a.OwnerId == orgId);
        Assert.Equal(0, acc.FreeCreditsGranted);

        await AssertLedgerInvariantAsync(tdb, OwnerType.Org, orgId);
    }

    /// <summary>
    /// Cấp 0 hoặc âm bị chặn ở CỬA. Hai lý do: (a) bút toán delta = 0 vi phạm CHECK
    /// <c>ck_credit_transactions_delta_nonzero</c> → ném từ TRONG transaction, đúng hình dạng DB20;
    /// (b) "cấp số âm" sẽ là một đường TRỪ credit không có bút toán đảo gắn khoản gốc — trừ credit phải
    /// đi đường hoàn tiền F18.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Cap_SoLuongKhongDuong_BiTuChoi_VaKhongDoiGiCa(int credits)
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 7);

        var result = await NewService(tdb).GrantAsync(OwnerType.User, ownerId, credits, "x", null, Admin);

        Assert.Equal(GrantOutcome.InvalidAmount, result.Outcome);

        await using var db = tdb.NewContext();
        Assert.Equal(7, (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == ownerId))
            .RemainingCredits);
        Assert.False(await db.CreditTransactions
            .AnyAsync(t => t.Reason == CreditTransactionReason.PromoGrant));
    }

    /// <summary>
    /// Ví bị Đình chỉ VẪN nhận được quà. PAY-12 chặn hành động TƯƠNG LAI (reserve), còn cộng tiền vào ví
    /// là chiều ngược lại — chặn nó chỉ khiến admin không đền bù được cho chính tài khoản đang có tranh
    /// chấp, tức đúng lúc cần nhất.
    /// </summary>
    [Fact]
    public async Task Cap_ChoViBiDinhChi_VanCap_ViDayLaCongTienChuKhongPhaiHanhDong()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 1,
            status: CreditAccountStatus.Suspended);

        var result = await NewService(tdb).GrantAsync(OwnerType.User, ownerId, 3, "đền bù", null, Admin);

        Assert.Equal(GrantOutcome.Granted, result.Outcome);
        Assert.Equal(4, result.RemainingCredits);

        await using var db = tdb.NewContext();
        // Vẫn Suspended — cấp quà KHÔNG được âm thầm gỡ lệnh đình chỉ.
        Assert.Equal(CreditAccountStatus.Suspended,
            (await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == ownerId)).Status);
    }

    /// <summary>Quà cộng vào <c>remaining</c>, KHÔNG đụng credit đang giữ cho buổi thi đang chạy.</summary>
    [Fact]
    public async Task Cap_KhongDungVaoCreditDangGiu()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 1, reserved: 2);

        await NewService(tdb).GrantAsync(OwnerType.User, ownerId, 5, "quà", null, Admin);

        await using var db = tdb.NewContext();
        var acc = await db.CreditAccounts.AsNoTracking().SingleAsync(a => a.OwnerId == ownerId);
        Assert.Equal(6, acc.RemainingCredits);
        Assert.Equal(2, acc.ReservedCredits);

        await AssertLedgerInvariantAsync(tdb, OwnerType.User, ownerId);
    }

    [Fact]
    public async Task R8_CungKey_CapMotLanVaReplayDungResponseBanDau()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 2);
        const string key = "grant-2026-07-27-001";

        var first = await NewService(tdb).GrantAsync(OwnerType.User, ownerId, 5, "đền bù", key, Admin);

        // Giao dịch khác sau lần grant đầu chứng minh retry không được đọc số dư hiện tại (10).
        await NewService(tdb).GrantAsync(OwnerType.User, ownerId, 3, "quà khác", null, Admin);
        var replay = await NewService(tdb).GrantAsync(OwnerType.User, ownerId, 999, "body retry khác", key, Admin);

        Assert.Equal(first, replay);
        await using var db = tdb.NewContext();
        Assert.Equal(1, await db.CreditTransactions.CountAsync(t =>
            t.OwnerType == OwnerType.User && t.OwnerId == ownerId && t.GrantIdempotencyKey == key));
        Assert.Equal(10, (await db.CreditAccounts.SingleAsync(a => a.OwnerId == ownerId)).RemainingCredits);
    }

    [Fact]
    public async Task R8_CungKeyKhacVi_KhongDedupCheo()
    {
        using var tdb = new PaymentTestDb();
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, firstOwner, remaining: 0);
        await SeedWalletAsync(tdb, OwnerType.User, secondOwner, remaining: 0);
        const string key = "same-client-key";

        var first = await NewService(tdb).GrantAsync(OwnerType.User, firstOwner, 2, "quà", key, Admin);
        var second = await NewService(tdb).GrantAsync(OwnerType.User, secondOwner, 4, "quà", key, Admin);

        Assert.NotEqual(first.TransactionId, second.TransactionId);
        Assert.Equal(2, first.RemainingCredits);
        Assert.Equal(4, second.RemainingCredits);
    }

    // ── controller: người cấp lấy từ JWT ─────────────────────────────────────────────────────

    /// <summary>
    /// Người cấp lấy từ JWT, KHÔNG từ body. Request DTO cố tình không có trường "grantedBy": cấp credit
    /// là in tiền, để client tự khai thì cột truy trách nhiệm thành lời khai của chính kẻ cần bị truy.
    /// </summary>
    [Fact]
    public async Task Controller_NguoiCapLayTuJwt_KhongPhaiTuBody()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 0);

        var controller = NewController(tdb, new Claim(ClaimTypes.NameIdentifier, Admin.ToString()));
        var result = await controller.GrantCredits(new GrantCreditRequest
        {
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            Credits = 3,
            Note = "khuyến mãi tháng 7",
        });

        var body = Assert.IsType<GrantCreditResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, body.CreditsGranted);

        await using var db = tdb.NewContext();
        var tx = await db.CreditTransactions.AsNoTracking()
            .SingleAsync(t => t.Reason == CreditTransactionReason.PromoGrant);
        Assert.Equal(Admin, tx.GrantedBy);

        // DTO request KHÔNG được có đường khai người cấp.
        Assert.Null(typeof(GrantCreditRequest).GetProperty("GrantedBy"));
    }

    [Fact]
    public async Task Controller_KhongSuyDuocDanhTinhAdmin_TuChoi()
    {
        using var tdb = new PaymentTestDb();
        var controller = NewController(tdb);   // không claim nào

        var result = await controller.GrantCredits(new GrantCreditRequest
        {
            OwnerType = OwnerType.User,
            OwnerId = Guid.NewGuid(),
            Credits = 3,
            Note = "quà",
        });

        Assert.IsType<ForbidResult>(result.Result);

        await using var db = tdb.NewContext();
        Assert.False(await db.CreditTransactions.AnyAsync());
    }

    // ── controller: admin đọc ví người khác ──────────────────────────────────────────────────

    [Fact]
    public async Task Controller_AdminDocDuocViNguoiKhac()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 9, reserved: 1);

        var controller = NewController(tdb, new Claim(ClaimTypes.NameIdentifier, Admin.ToString()));
        var result = await controller.GetAccount(OwnerType.User, ownerId);

        var acc = Assert.IsType<CreditAccountResponse>(result.Value);
        Assert.Equal(ownerId, acc.OwnerId);
        Assert.Equal(9, acc.RemainingCredits);
        Assert.Equal(1, acc.ReservedCredits);
    }

    [Fact]
    public async Task Controller_ViChuaTonTai_Tra0Credit_KhongPhai404()
    {
        using var tdb = new PaymentTestDb();
        var controller = NewController(tdb, new Claim(ClaimTypes.NameIdentifier, Admin.ToString()));

        var result = await controller.GetAccount(OwnerType.User, Guid.NewGuid());

        var acc = Assert.IsType<CreditAccountResponse>(result.Value);
        Assert.Equal(0, acc.RemainingCredits);
    }

    /// <summary>
    /// Sổ cái bản admin lộ <c>granted_by</c>/<c>note</c> (ai cấp quà, vì sao) — thứ bản của chủ ví CỐ Ý
    /// không trả, vì "nhân viên nào bấm nút" là thông tin vận hành nội bộ.
    /// </summary>
    [Fact]
    public async Task Controller_SoCaiBanAdmin_LoNguoiCapVaLyDo()
    {
        using var tdb = new PaymentTestDb();
        var ownerId = Guid.NewGuid();
        await SeedWalletAsync(tdb, OwnerType.User, ownerId, remaining: 0);
        await NewService(tdb).GrantAsync(OwnerType.User, ownerId, 5, "đền bù sự cố", null, Admin);

        var controller = NewController(tdb, new Claim(ClaimTypes.NameIdentifier, Admin.ToString()));
        var result = await controller.GetTransactions(OwnerType.User, ownerId);

        var rows = Assert.IsType<List<CreditTransactionResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        var promo = rows.Single(r => r.Reason == CreditTransactionReason.PromoGrant);
        Assert.Equal(Admin, promo.GrantedBy);
        Assert.Equal("đền bù sự cố", promo.Note);

        // Bản của chủ ví KHÔNG lộ hai trường đó.
        var chuVi = new CreditAccountController(tdb.Db, Options.Create(new BillingSettings()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, ownerId.ToString())], "test")),
                },
            },
        };
        var mine = Assert.IsType<List<CreditTransactionResponse>>(
            Assert.IsType<OkObjectResult>((await chuVi.GetMyCreditTransactionsAsync()).Result).Value);

        Assert.All(mine, r =>
        {
            Assert.Null(r.GrantedBy);
            Assert.Null(r.Note);
        });
    }
}
