using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

public sealed class CampaignEntitlementTests
{
    private sealed class Entitlements(CampaignEntitlement value) : IEntitlementClient
    {
        public Task<CampaignEntitlement> ResolveOrgAsync(Guid orgId, CancellationToken ct = default) => Task.FromResult(value);
    }

    private static readonly CampaignEntitlement Business = new("resolved", "business", 1, 10, 200, true, true, true);
    private static CampaignSvc Service(CampaignDbContext db, CampaignEntitlement entitlement) => new(
        db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
        Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), entitlements: new Entitlements(entitlement));

    private static CreateCampaignRequest Request(int? cap = 25) => new()
    {
        Title = "T8", Domain = "BE", MaxCandidates = cap, TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddMinutes(1), ExpiresAt = DateTime.UtcNow.AddDays(1),
        Questions = [new QuestionItem { QuestionText = "Q" }]
    };

    [Fact]
    public async Task Starter_SecondActiveCampaign_IsForbidden()
    {
        using var tdb = new CampaignTestDb(); var db = tdb.NewContext(); var org = Guid.NewGuid();
        db.Campaigns.Add(new Campaign { Id = Guid.NewGuid(), OrgId = org, Title = "active", Status = CampaignStatus.Active, StartsAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<EntitlementForbiddenException>(() => Service(db, CampaignEntitlement.Starter).CreateCampaignAsync(org, org, Request(), default));
    }

    [Fact]
    public async Task Starter_SecondDraftCannotBypassActiveCampaignCapWhenPublished()
    {
        using var tdb = new CampaignTestDb(); 
        var org = Guid.NewGuid();
        var first = await Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(), default);
        var second = await Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(), default);

        await Service(tdb.NewContext(), CampaignEntitlement.Starter).PublishCampaignAsync(org, org, first.Id, default);
        await Assert.ThrowsAsync<EntitlementForbiddenException>(() =>
            Service(tdb.NewContext(), CampaignEntitlement.Starter).PublishCampaignAsync(org, org, second.Id, default));

        using var read = tdb.NewContext();
        Assert.Equal(CampaignStatus.Draft, (await read.Campaigns.FindAsync(second.Id))!.Status);
    }

    [Fact]
    public async Task Business_CreateWithinCap_Succeeds_AndOverCapFails()
    {
        using var tdb = new CampaignTestDb(); var org = Guid.NewGuid();
        var ok = await Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(200), default);
        Assert.Equal(200, ok.MaxCandidates);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(201), default));
    }

    /// <summary>
    /// ĐẢO TIỀN ĐỀ có chủ đích: adaptive nay CHO PHÉP ở mọi tier — mỗi buổi B2B tiêu 1 credit ví org
    /// bất kể gói (PAY-6), nên gói không lấy mất engine mà org vừa trả tiền để chạy. Grounding thì
    /// KHÁC (thêm retrieval + token) nên vẫn bị chặn ⇒ test giữ cả hai vế để không ai gỡ nhầm cái còn lại.
    ///
    /// Dùng gói fail-closed nghèo nhất (<c>Starter</c>, cũng là fallback khi Payment sập) để nếu ai đó
    /// dựng lại chốt chặn adaptive thì đúng ca này ĐỎ.
    /// </summary>
    [Fact]
    public async Task Starter_AllowsAdaptive_ButStillBlocksGrounding_CreateAndUpdate()
    {
        using var tdb = new CampaignTestDb(); var db = tdb.NewContext(); var org = Guid.NewGuid();
        var adaptive = Request(); adaptive.AdaptiveEnabled = true;
        var created = await Service(db, CampaignEntitlement.Starter).CreateCampaignAsync(org, org, adaptive, default);
        Assert.True(created.AdaptiveEnabled);
        Assert.True(db.Campaigns.Single(c => c.Id == created.Id).AdaptiveEnabled);

        var updated = await Service(db, CampaignEntitlement.Starter).UpdateCampaignAsync(
            org, org, created.Id, new UpdateCampaignRequest { Title = "T8", AdaptiveEnabled = true }, default);
        Assert.True(updated.AdaptiveEnabled);

        var grounding = Request(); grounding.GroundingEnabled = true;
        await Assert.ThrowsAsync<EntitlementForbiddenException>(() =>
            Service(db, CampaignEntitlement.Starter).CreateCampaignAsync(org, org, grounding, default));
        await Assert.ThrowsAsync<EntitlementForbiddenException>(() =>
            Service(db, CampaignEntitlement.Starter).UpdateCampaignAsync(org, org, created.Id, new UpdateCampaignRequest { GroundingEnabled = true }, default));
    }

    /// <summary>
    /// Ca THẬT còn hở sau khi bỏ gate: plan do admin tạo qua UI mặc định <c>adaptiveEnabled = false</c>
    /// (`plan-form-dialog.ts`) ⇒ entitlement trả về `false` một cách hợp lệ. Luật INT-19 nói HR vẫn phải
    /// bật được adaptive.
    ///
    /// Vì sao phải có test riêng chứ không dựa vào <see cref="Starter_AllowsAdaptive_ButStillBlocksGrounding_CreateAndUpdate"/>:
    /// `Starter` nay mang <c>AdaptiveEnabled = true</c>, nên nếu ai dựng lại chốt chặn thì điều kiện
    /// `!entitlement.AdaptiveEnabled` **khớp 0 row và chặn hộ** — mutation "dựng lại gate" chạy qua XANH.
    /// Một lớp che lớp kia; chỉ entitlement `false` mới phân biệt được "đã gỡ gate" với "fallback nói true".
    /// </summary>
    [Fact]
    public async Task GoiKhaiAdaptiveFalse_VanKhongBiChanAdaptive()
    {
        using var tdb = new CampaignTestDb(); var db = tdb.NewContext(); var org = Guid.NewGuid();
        var noAdaptive = new CampaignEntitlement("resolved", "custom", 1, 10, 200, false, false, false);

        var req = Request(); req.AdaptiveEnabled = true;
        var created = await Service(db, noAdaptive).CreateCampaignAsync(org, org, req, default);
        Assert.True(created.AdaptiveEnabled);

        var updated = await Service(db, noAdaptive).UpdateCampaignAsync(
            org, org, created.Id, new UpdateCampaignRequest { Title = "T8", AdaptiveEnabled = true }, default);
        Assert.True(updated.AdaptiveEnabled);
    }

    /// <summary>
    /// Khoá GIÁ TRỊ (không phải hành vi) của hai projection fallback: sau khi bỏ chốt chặn adaptive,
    /// <c>AdaptiveEnabled</c> không còn consumer nào trong Campaign ⇒ nó dễ bị đặt lại thành
    /// <c>false</c> mà không test nào kêu, rồi từ đó nói dối bất kỳ consumer nào đọc sau này.
    /// Hai fallback phải nói ĐÚNG luật: adaptive có ở mọi tier.
    /// </summary>
    [Fact]
    public void FallbackProjections_KhongNoiDoiVeAdaptive()
    {
        Assert.True(CampaignEntitlement.Starter.AdaptiveEnabled);
        Assert.True(CampaignEntitlement.Legacy.AdaptiveEnabled);
        // Những quyền lợi CÓ chi phí biên khác nhau thật vẫn fail-closed ở fallback nghèo nhất.
        Assert.False(CampaignEntitlement.Starter.GroundingEnabled);
        Assert.False(CampaignEntitlement.Starter.PostpaidEligible);
    }

    [Fact]
    public async Task Starter_FallbackCapsInviteAndScreening()
    {
        using var tdb = new CampaignTestDb(); var db = tdb.NewContext(); var org = Guid.NewGuid();
        var campaignResponse = await Service(db, Business).CreateCampaignAsync(org, org, Request(200), default);
        var campaign = db.Campaigns.Single(c => c.Id == campaignResponse.Id); campaign.Status = CampaignStatus.Active;
        for (var i = 0; i < 25; i++) db.CampaignInvitations.Add(new CampaignInvitation { Id = Guid.NewGuid(), CampaignId = campaign.Id, TokenHash = Guid.NewGuid().ToString(), Email = $"{i}@x.test", ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db, CampaignEntitlement.Starter).CreateInvitationsAsync(org, org, campaign.Id, ["next@x.test"], default));

        db.CampaignInvitations.RemoveRange(db.CampaignInvitations);
        for (var i = 0; i < 25; i++) db.CvSubmissions.Add(new CvSubmission { Id = Guid.NewGuid(), CampaignId = campaign.Id, Status = CvSubmissionStatus.Filtered, ParseStatus = CvParseStatus.Done, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var files = new FormFileCollection { new FormFile(new MemoryStream([1]), 0, 1, "files", "cv.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" } };
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db, CampaignEntitlement.Starter).ScreenCandidatesAsync(org, org, campaign.Id, files, default));
    }
}
