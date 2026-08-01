using System.Text.Json;
using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Khoá những vế vừa được vá ở PR #124 mà chưa có test nào giữ:
/// hợp đồng JSON của entitlement snapshot · idempotency grant theo owner · bất biến tiền ở
/// đường fallback metered→credit · settle đúng meter khi upgrade thật / khi crash giữa chừng.
/// </summary>
public class TierContractGuardTests
{
    private static CreditAccountService Service(PaymentDbContext db) => new(
        db, subscriptions: new SubscriptionService(db), entitlements: new EntitlementResolver(db),
        tiering: Options.Create(new TieringSettings { Enabled = true }));

    private static async Task<Guid> WalletAsync(PaymentTestDb t, OwnerType type, int credits = 0)
    {
        var owner = Guid.NewGuid();
        t.Db.CreditAccounts.Add(new CreditAccount
        {
            Id = Guid.NewGuid(), OwnerType = type, OwnerId = owner, PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active, RemainingCredits = credits, UpdatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();
        return owner;
    }

    // ── B2: HỢP ĐỒNG JSON (vế PRODUCER) ────────────────────────────────────────────────────────
    // Consumer (Interview/Campaign) deserialize snapshot bằng record của riêng nó. Lệch tên field
    // KHÔNG ném lỗi — System.Text.Json chỉ điền default (0/false) ⇒ hỏng ÂM THẦM. Đây là bug tái
    // diễn lần 2 trong repo (trước: focusCriteria/BC14). Khoá đúng bộ khoá camelCase mà consumer đọc.
    [Theory]
    [InlineData("adaptiveEnabled")]
    [InlineData("adaptiveMaxQuestions")]   // Interview: trần số câu — từng bị đọc nhầm là "maxQuestions" ⇒ luôn 0
    [InlineData("adaptiveMaxFollowups")]   // Interview: trần follow-up — từng bị đọc nhầm là "maxFollowUps"
    [InlineData("groundingEnabled")]
    [InlineData("selfConsistencyN")]
    [InlineData("cvAnalysisIncluded")]
    [InlineData("repoAnalysisIncluded")]
    [InlineData("roadmapEnabled")]
    [InlineData("maxActiveCampaigns")]     // Campaign
    [InlineData("maxCandidatesCap")]       // Campaign
    [InlineData("postpaidEligible")]       // Campaign + AdminCreditService
    public void SnapshotJson_GiuNguyenTenFieldMaConsumerDoc(string requiredKey)
    {
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Audience = PlanAudience.B2C, Code = "pro", Name = "Pro", Rank = 2,
            InterviewFunding = InterviewFunding.Metered, MonthlyQuota = 100,
            AdaptiveEnabled = true, AdaptiveMaxQuestions = 20, AdaptiveMaxFollowups = 5,
            GroundingEnabled = true, SelfConsistencyN = 3, RepoAnalysisIncluded = true, RoadmapEnabled = true
        };

        using var doc = JsonDocument.Parse(EntitlementSnapshot.Create(plan).Json);

        Assert.True(doc.RootElement.TryGetProperty(requiredKey, out _),
            $"Snapshot thiếu khoá '{requiredKey}' — consumer sẽ nhận default (0/false) mà KHÔNG có lỗi nào.");
    }

    // Vế giá trị: gói Pro phải ra đúng 20/5, không phải 0 (đúng bug đã xảy ra).
    [Fact]
    public void SnapshotJson_GoiPro_MangDungTranThat()
    {
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Audience = PlanAudience.B2C, Code = "pro", Name = "Pro", Rank = 2,
            InterviewFunding = InterviewFunding.Metered, MonthlyQuota = 100,
            AdaptiveEnabled = true, AdaptiveMaxQuestions = 20, AdaptiveMaxFollowups = 5
        };

        using var doc = JsonDocument.Parse(EntitlementSnapshot.Create(plan).Json);

        Assert.Equal(20, doc.RootElement.GetProperty("adaptiveMaxQuestions").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("adaptiveMaxFollowups").GetInt32());
    }

    // ── B5: idempotency grant phải scope theo OWNER ────────────────────────────────────────────
    // Key toàn cục (bug cũ) làm grant thứ 2 trả về subscription CỦA NGƯỜI KHÁC: khách B không nhận
    // được gì, còn response lộ ownerId/subscriptionId của khách A. Repo đã sửa đúng lỗi này cho
    // credit grant (R8) — subscription grant từng regress lại.
    [Fact]
    public async Task Grant_CungKey_KhacOwner_TaoHaiSubscriptionDocLap()
    {
        using var t = new PaymentTestDb();
        var a = await WalletAsync(t, OwnerType.User);
        var b = await WalletAsync(t, OwnerType.User);
        var plan = await t.NewContext().Plans.SingleAsync(p => p.Code == "plus" && p.Audience == PlanAudience.B2C);

        var subA = await new SubscriptionService(t.NewContext()).GrantAsync(OwnerType.User, a, plan.Id, 30, null, "promo-thang-8");
        var subB = await new SubscriptionService(t.NewContext()).GrantAsync(OwnerType.User, b, plan.Id, 30, null, "promo-thang-8");

        Assert.NotEqual(subA.Id, subB.Id);
        Assert.Equal(a, subA.OwnerId);
        Assert.Equal(b, subB.OwnerId);   // KHÔNG được trả về subscription của owner A
        using var read = t.NewContext();
        Assert.Equal(2, await read.Subscriptions.CountAsync(s => s.AdminGrantIdempotencyKey == "promo-thang-8"));
    }

    // Cùng owner + cùng key vẫn phải idempotent (không được vì sửa mà mất tính chất cũ).
    [Fact]
    public async Task Grant_CungKey_CungOwner_VanIdempotent()
    {
        using var t = new PaymentTestDb();
        var a = await WalletAsync(t, OwnerType.User);
        var plan = await t.NewContext().Plans.SingleAsync(p => p.Code == "plus" && p.Audience == PlanAudience.B2C);
        var svc = new SubscriptionService(t.NewContext());

        var first = await svc.GrantAsync(OwnerType.User, a, plan.Id, 30, null, "same-owner");
        var second = await svc.GrantAsync(OwnerType.User, a, plan.Id, 30, null, "same-owner");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await t.NewContext().Subscriptions.Where(s => s.AdminGrantIdempotencyKey == "same-owner").ToListAsync());
    }

    // ── Bất biến tiền ở đường fallback metered → credit ────────────────────────────────────────
    // Ví được seed KÈM bút toán để `remaining + reserved = Σ delta` assert được (fixture cũ seed
    // RemainingCredits trần nên bất biến vốn đã sai từ đầu, không khoá được gì).
    [Fact]
    public async Task HetQuota_RoiVeCredit_VanGiuBatBienSoDu()
    {
        using var t = new PaymentTestDb();
        var owner = Guid.NewGuid(); var now = DateTime.UtcNow;
        t.Db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active, RemainingCredits = 2, UpdatedAt = now });
        t.Db.CreditTransactions.Add(new CreditTransaction { Id = Guid.NewGuid(), OwnerType = OwnerType.User,
            OwnerId = owner, Delta = 2, Reason = CreditTransactionReason.Purchase, CreatedAt = now });
        t.Db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            Audience = PlanAudience.B2C, TierCode = "plus", TierRank = 1, InterviewFunding = InterviewFunding.Metered,
            MonthlyQuota = 1, EntitlementSnapshot = "{}", EntitlementHash = "x", ActivatedAt = now.AddMinutes(-1),
            StartedAt = now.AddMinutes(-1), ExpiresAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now });
        await t.Db.SaveChangesAsync();

        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, Guid.NewGuid()); // quota
        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, Guid.NewGuid()); // hết quota → credit

        using var read = t.NewContext();
        var acc = await read.CreditAccounts.SingleAsync();
        var ledger = await read.CreditTransactions.SumAsync(x => x.Delta);
        Assert.Equal(ledger, acc.RemainingCredits + acc.ReservedCredits);
        Assert.Equal(1, await read.CreditReservations.CountAsync(r => r.FundedBy == ReservationFunding.SubscriptionMetered));
        Assert.Equal(1, await read.CreditReservations.CountAsync(r => r.FundedBy == ReservationFunding.Credit));
    }

    // ── Upgrade THẬT (sub MỚI, rank cao hơn) giữa buổi ─────────────────────────────────────────
    // Khác test đang có: test cũ chỉ flip field trên CÙNG row. Ở đây resolver sẽ trả sub Pro mới,
    // nhưng settle phải trừ đúng meter của sub Plus đã ghi cứng lên reservation lúc reserve.
    [Fact]
    public async Task UpgradeThat_GiuaBuoi_ConsumeVaoDungMeterCu()
    {
        using var t = new PaymentTestDb();
        var owner = Guid.NewGuid(); var now = DateTime.UtcNow; var session = Guid.NewGuid();
        t.Db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active, RemainingCredits = 0, UpdatedAt = now });
        var plus = new Subscription { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            Audience = PlanAudience.B2C, TierCode = "plus", TierRank = 1, InterviewFunding = InterviewFunding.Metered,
            MonthlyQuota = 30, EntitlementSnapshot = "{}", EntitlementHash = "x", ActivatedAt = now.AddMinutes(-5),
            StartedAt = now.AddMinutes(-5), ExpiresAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now };
        t.Db.Subscriptions.Add(plus);
        await t.Db.SaveChangesAsync();

        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, session);

        // Upgrade: thêm row Pro rank cao hơn ⇒ resolver từ giờ trả Pro.
        t.Db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            Audience = PlanAudience.B2C, TierCode = "pro", TierRank = 2, InterviewFunding = InterviewFunding.Metered,
            MonthlyQuota = 100, EntitlementSnapshot = "{}", EntitlementHash = "x", ActivatedAt = now,
            StartedAt = now, ExpiresAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now });
        await t.Db.SaveChangesAsync();

        await Service(t.NewContext()).ConsumeAsync(session);

        using var read = t.NewContext();
        var meter = await read.SubscriptionMeters.SingleAsync(m => m.SubscriptionId == plus.Id);
        Assert.Equal(1, meter.UsedCount);       // trừ vào meter của Plus (sub lúc reserve)
        Assert.Equal(0, meter.ReservedCount);
        Assert.Empty(await read.CreditTransactions.ToListAsync());   // metered không ghi ledger
    }

    // ── Crash giữa reserve ↔ consume ───────────────────────────────────────────────────────────
    // Mô phỏng: reservation đã Reserved nhưng counter meter bị mất (process chết trước khi ghi).
    // Reconciler phải dựng lại reserved_count từ chính các reservation.
    [Fact]
    public async Task CrashGiuaReserveVaConsume_ReconcilerDungLaiCounter()
    {
        using var t = new PaymentTestDb();
        var owner = Guid.NewGuid(); var now = DateTime.UtcNow;
        t.Db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            PaymentMode = PaymentMode.Prepaid, Status = CreditAccountStatus.Active, RemainingCredits = 0, UpdatedAt = now });
        var sub = new Subscription { Id = Guid.NewGuid(), OwnerType = OwnerType.User, OwnerId = owner,
            Audience = PlanAudience.B2C, TierCode = "plus", TierRank = 1, InterviewFunding = InterviewFunding.Metered,
            MonthlyQuota = 30, EntitlementSnapshot = "{}", EntitlementHash = "x", ActivatedAt = now.AddMinutes(-1),
            StartedAt = now.AddMinutes(-1), ExpiresAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now };
        t.Db.Subscriptions.Add(sub);
        await t.Db.SaveChangesAsync();

        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());
        await Service(t.NewContext()).ReserveAsync(OwnerType.User, owner, Guid.NewGuid());

        // "Crash": counter bốc hơi, reservation vẫn còn.
        await t.Db.SubscriptionMeters.Where(m => m.SubscriptionId == sub.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReservedCount, 0));

        var (r, provider) = SubscriptionMeterReconcilerHarness.Build(t);
        using (provider) { await SubscriptionMeterReconcilerHarness.ScanOnce(r); }

        using var read = t.NewContext();
        Assert.Equal(2, (await read.SubscriptionMeters.SingleAsync(m => m.SubscriptionId == sub.Id)).ReservedCount);
    }
}
