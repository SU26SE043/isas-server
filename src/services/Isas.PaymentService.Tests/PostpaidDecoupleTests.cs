using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using static Isas.PaymentService.Services.IAdminCreditService;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Tách Postpaid khỏi gói thuê bao — postpaid nay là thoả thuận do PlatformAdmin duyệt.
///
/// Trước vòng này, bật Postpaid đòi org phải có plan `postpaid_eligible=true` (chỉ business/enterprise
/// mới có), và khối kiểm tra đó KHÔNG nằm sau cờ `Tiering:Enabled` ⇒ trên prod (0 subscription B2B,
/// mọi org rơi về `starter`) lệnh duyệt trả 403 cho gần như mọi tổ chức. Nay gate đã gỡ, và ví
/// Postpaid được mở khoá toàn bộ tính năng bằng cách lấy plan `enterprise` làm chuẩn.
/// </summary>
public class PostpaidDecoupleTests
{
    private static async Task<CreditAccount> SeedOrgWalletAsync(
        PaymentTestDb tdb, Guid orgId, PaymentMode mode, int remaining = 0, int? creditLimit = null)
    {
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.Org,
            OwnerId = orgId,
            PaymentMode = mode,
            Status = CreditAccountStatus.Active,
            RemainingCredits = remaining,
            ReservedCredits = 0,
            CreditLimit = creditLimit,
            PeriodUsage = mode == PaymentMode.Postpaid ? 0 : null,
            UpdatedAt = DateTime.UtcNow,
        };
        tdb.Db.CreditAccounts.Add(acc);
        if (remaining > 0)
            tdb.Db.CreditTransactions.Add(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                OwnerType = OwnerType.Org,
                OwnerId = orgId,
                Delta = remaining,
                Reason = CreditTransactionReason.Purchase,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            });
        await tdb.Db.SaveChangesAsync();
        return acc;
    }

    private static AdminCreditService NewAdmin(PaymentTestDb tdb) =>
        new(tdb.Db, new CreditAccountService(
            tdb.Db, null, Options.Create(new BillingSettings { FreeTrialCredits = 0 })));

    // ── EntitlementResolver: ví Postpaid mở khoá full feature ────────────────────────────────

    /// <summary>
    /// BẤT BIẾN TIỀN — `InterviewFunding` PHẢI là `Credit`.
    ///
    /// Nếu ví postpaid trả về `Unlimited`, `ReserveAsync` sẽ rẽ sang nhánh `Subscription`: nhánh đó
    /// KHÔNG cộng `period_usage` và bỏ qua guard hoá đơn quá hạn ⇒ org dùng thoải mái mà hoá đơn cuối
    /// kỳ ra 0 đồng (`invoice.interview_count` lấy snapshot từ chính `period_usage`).
    /// </summary>
    [Fact]
    public async Task ViPostpaid_MoKhoaEnterprise_VaGiuFundingCredit()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedOrgWalletAsync(tdb, orgId, PaymentMode.Postpaid, creditLimit: 20);

        var result = await new EntitlementResolver(tdb.NewContext()).ResolveAsync(OwnerType.Org, orgId);

        Assert.Equal("postpaid", result.Source);
        Assert.Equal("enterprise", result.TierCode);
        Assert.Equal(PlanAudience.B2B, result.Audience);
        Assert.Equal(InterviewFunding.Credit, result.InterviewFunding);
        Assert.Null(result.MonthlyQuota);
        // Full feature: enterprise không giới hạn số campaign/ứng viên, có adaptive + grounding.
        Assert.Contains("\"adaptiveEnabled\":true", result.EntitlementSnapshot);
        Assert.Contains("\"groundingEnabled\":true", result.EntitlementSnapshot);
        Assert.Contains("\"maxActiveCampaigns\":null", result.EntitlementSnapshot);
    }

    /// <summary>
    /// Postpaid là hợp đồng, phải THẮNG gói self-serve đang có — nếu không, org vừa ký hợp đồng trả
    /// sau vừa còn gói `starter` sẽ bị bó theo hạn mức của starter.
    /// </summary>
    [Fact]
    public async Task ViPostpaid_ThangCaSubscriptionDangCoHieuLuc()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SeedOrgWalletAsync(tdb, orgId, PaymentMode.Postpaid, creditLimit: 20);
        tdb.Db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(), OwnerType = OwnerType.Org, OwnerId = orgId,
            Audience = PlanAudience.B2B, TierCode = "starter", TierRank = 0,
            InterviewFunding = InterviewFunding.Credit, EntitlementSnapshot = "{}", EntitlementHash = "x",
            ActivatedAt = now.AddMinutes(-1), StartedAt = now.AddMinutes(-1), ExpiresAt = now.AddDays(30),
            CreatedAt = now, UpdatedAt = now,
        });
        await tdb.Db.SaveChangesAsync();

        var result = await new EntitlementResolver(tdb.NewContext()).ResolveAsync(OwnerType.Org, orgId);

        Assert.Equal("postpaid", result.Source);
        Assert.Equal("enterprise", result.TierCode);
        Assert.Equal(InterviewFunding.Credit, result.InterviewFunding);
    }

    /// <summary>Ví Prepaid KHÔNG được đổi hành vi — chống hồi quy cho toàn bộ org đang chạy.</summary>
    [Fact]
    public async Task ViPrepaid_KhongDoiHanhVi_VanStarter()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedOrgWalletAsync(tdb, orgId, PaymentMode.Prepaid, remaining: 5);

        var result = await new EntitlementResolver(tdb.NewContext()).ResolveAsync(OwnerType.Org, orgId);

        Assert.Equal("starter", result.TierCode);
        Assert.NotEqual("postpaid", result.Source);
    }

    /// <summary>
    /// Catalog bị xoá/sửa hỏng thì vẫn phải lùi về entitlement biên dịch sẵn, KHÔNG được ném — đường
    /// này nằm trên hot path của `ReserveAsync` (mẫu đã có sẵn ở nhánh free plan).
    /// </summary>
    [Fact]
    public async Task ViPostpaid_CatalogRong_LuiVeSeedEnterprise()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedOrgWalletAsync(tdb, orgId, PaymentMode.Postpaid, creditLimit: 20);
        tdb.Db.Plans.RemoveRange(tdb.Db.Plans);
        await tdb.Db.SaveChangesAsync();

        var result = await new EntitlementResolver(tdb.NewContext()).ResolveAsync(OwnerType.Org, orgId);

        Assert.Equal("enterprise", result.TierCode);
        Assert.Equal(InterviewFunding.Credit, result.InterviewFunding);
    }

    // ── AdminCreditService: duyệt Postpaid không còn hỏi gói thuê bao ────────────────────────

    /// <summary>
    /// Đây là hành vi ĐỔI: org không có subscription nào (rơi về `starter`, `postpaid_eligible=false`)
    /// trước đây bị trả `PostpaidNotEligible`; nay admin duyệt được.
    /// </summary>
    [Fact]
    public async Task DuyetPostpaid_OrgKhongCoGoiThueBao_VanDuocDuyet()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedOrgWalletAsync(tdb, orgId, PaymentMode.Prepaid);

        var result = await NewAdmin(tdb).SetPaymentModeAsync(
            OwnerType.Org, orgId, PaymentMode.Postpaid, creditLimit: 10,
            note: "hợp đồng trả sau", allowStrandedCredits: false, adminUserId: Guid.NewGuid());

        Assert.Equal(SetPaymentModeOutcome.Updated, result.Outcome);
        var acc = await tdb.NewContext().CreditAccounts.AsNoTracking()
            .SingleAsync(a => a.OwnerType == OwnerType.Org && a.OwnerId == orgId);
        Assert.Equal(PaymentMode.Postpaid, acc.PaymentMode);
        Assert.Equal(10, acc.CreditLimit);
        Assert.Equal(0, acc.PeriodUsage);
    }

    /// <summary>
    /// ⚠ ĐỔI TIỀN ĐỀ CÓ CHỦ ĐÍCH: bản cũ khoá guard `StrandedCredits` — chặn nâng mode khi ví còn
    /// credit đã mua, vì hồi đó nhánh đặt chỗ postpaid không đụng `remaining` nên credit sẽ kẹt cứng.
    /// Nay ví Postpaid TIÊU HẾT credit đã mua trước rồi mới dồn nợ kỳ, nên không còn gì để kẹt và
    /// chặn admin là vô nghĩa — guard đã gỡ.
    ///
    /// Test này nay khoá hệ quả MẠNH HƠN cái cũ: credit không những sống sót qua lần đổi mode, mà
    /// còn phải THỰC SỰ TIÊU ĐƯỢC sau đó (chứ không chỉ nằm trong cột `remaining` cho đẹp).
    /// </summary>
    [Fact]
    public async Task DuyetPostpaid_ViConCreditDaMua_ChoQua_VaCreditVanTieuDuoc()
    {
        using var tdb = new PaymentTestDb();
        var orgId = Guid.NewGuid();
        await SeedOrgWalletAsync(tdb, orgId, PaymentMode.Prepaid, remaining: 5);

        var result = await NewAdmin(tdb).SetPaymentModeAsync(
            OwnerType.Org, orgId, PaymentMode.Postpaid, creditLimit: 10,
            note: "hợp đồng trả sau", allowStrandedCredits: false, adminUserId: Guid.NewGuid());

        Assert.Equal(SetPaymentModeOutcome.Updated, result.Outcome);
        var afterUpgrade = await tdb.NewContext().CreditAccounts.AsNoTracking()
            .SingleAsync(a => a.OwnerType == OwnerType.Org && a.OwnerId == orgId);
        Assert.Equal(PaymentMode.Postpaid, afterUpgrade.PaymentMode);
        Assert.Equal(5, afterUpgrade.RemainingCredits);   // credit KHÔNG bị xoá

        // …và tiêu được thật: buổi kế trừ vào credit đã mua, KHÔNG dồn nợ kỳ.
        var sessionId = Guid.NewGuid();
        await new CreditAccountService(tdb.NewContext()).ReserveAsync(OwnerType.Org, orgId, sessionId);
        await new CreditAccountService(tdb.NewContext()).ConsumeAsync(sessionId);

        var afterUse = await tdb.NewContext().CreditAccounts.AsNoTracking()
            .SingleAsync(a => a.OwnerType == OwnerType.Org && a.OwnerId == orgId);
        Assert.Equal(4, afterUse.RemainingCredits);
        Assert.Equal(0, afterUse.PeriodUsage);            // chưa phát sinh nợ kỳ nào
    }

    /// <summary>Ví cá nhân vẫn LUÔN Prepaid (D15) — gỡ gate thuê bao không mở cửa cho B2C.</summary>
    [Fact]
    public async Task DuyetPostpaid_ViCaNhan_VanChan()
    {
        using var tdb = new PaymentTestDb();
        var result = await NewAdmin(tdb).SetPaymentModeAsync(
            OwnerType.User, Guid.NewGuid(), PaymentMode.Postpaid, creditLimit: 10,
            note: "x", allowStrandedCredits: false, adminUserId: Guid.NewGuid());
        Assert.Equal(SetPaymentModeOutcome.NotOrg, result.Outcome);
    }
}
