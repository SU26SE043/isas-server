using System.Data.Common;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Cận DƯỚI của <c>campaigns.max_candidates</c> — <see cref="MaxCandidatesRule"/>.
///
/// <para><b>Bug được khoá.</b> Trước bản vá, <c>maxCandidates ≤ 0</c> đi lọt CẢ HAI đường ghi và
/// khoá vĩnh viễn mọi lời mời: <c>EnsureCandidateCapacityAsync</c> đọc trần đó thành
/// <c>effectiveCap = Math.Min(-5, planCap) = -5</c> ⇒ ngay lời mời ĐẦU TIÊN đã là <c>0 + 1 &gt; -5</c>
/// ⇒ ném. <c>0</c> cũng vậy. Chiến dịch tạo ra không bao giờ mời được ai, mà thông báo lúc đó
/// (<i>"Vượt giới hạn lời mời hiệu lực (-5)…"</i>) không chỉ ra nguyên nhân là con số HR đã nhập lúc tạo.</para>
///
/// <para><b>HAI lỗ ĐỘC LẬP, không phải một.</b> Nhánh create kiểm <c>is &gt; 0 &amp;&amp; &gt; cap</c>
/// — vế <c>&gt; 0</c> ở đó chỉ MIỄN cho số ≤ 0 khỏi phép so trần, không hề chặn chúng. Nhánh update
/// thì <b>chưa bao giờ có</b> vế đó, nó kiểm <c>HasValue &amp;&amp; &gt; cap</c> và <c>-5 &gt; 25</c> là
/// false nên cũng cho qua. Hai đường hỏng vì cùng một lý do (thiếu cận dưới) nhưng bằng hai đoạn mã
/// khác nhau ⇒ test chỉ phủ create là bỏ sót nguyên một đường ghi. Đó là lý do mọi ca dưới đây đều
/// có cặp create/update.</para>
///
/// <para><b>Vì sao assert "KHÔNG ghi gì" chứ không chỉ assert "có ném".</b> Ném mà vẫn ghi là một bug
/// KHÁC (campaign nửa vời / trần đã bị đổi rồi mới báo lỗi), và <c>Assert.ThrowsAsync</c> không phân
/// biệt được hai ca đó. Mỗi ca âm vì thế kiểm hai vế: <see cref="SaveSpy"/> chứng minh không có lượt
/// ghi nào được thử, và một <c>DbContext</c> MỚI chứng minh trạng thái trên đĩa không đổi.</para>
/// </summary>
public sealed class CampaignMaxCandidatesLowerBoundTests
{
    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private sealed class Entitlements(CampaignEntitlement value) : IEntitlementClient
    {
        public Task<CampaignEntitlement> ResolveOrgAsync(Guid orgId, CancellationToken ct = default)
            => Task.FromResult(value);
    }

    /// <summary>
    /// Đếm lượt <c>SaveChanges</c> ĐƯỢC THỬ. Đọc lại DB chỉ chứng minh *kết quả* giống nhau; cái cần
    /// chứng minh là *không có lượt ghi nào xảy ra* (một lượt ghi rồi rollback vẫn để lại hệ quả khác:
    /// audit log, outbox, sequence…).
    /// </summary>
    private sealed class SaveSpy : ISaveChangesInterceptor
    {
        public int Saves;

        public InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            Saves++;
            return result;
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Saves++;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Gói rộng rãi: trần ứng viên 200 ⇒ mọi giá trị dùng ở đây đều dưới cận TRÊN,
    /// nên ca âm chỉ có thể đỏ vì cận DƯỚI.</summary>
    private static readonly CampaignEntitlement Business =
        new("resolved", "business", 1, 10, 200, true, true, true);

    private static CampaignSvc Service(CampaignDbContext db, CampaignEntitlement entitlement) => new(
        db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
        Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
        entitlements: new Entitlements(entitlement));

    private static CreateCampaignRequest Request(int? maxCandidates) => new()
    {
        Title = "Tuyển BE",
        Domain = "BE",
        MaxCandidates = maxCandidates,
        TimeLimitMinutes = 30,
        StartsAt = DateTime.UtcNow.AddMinutes(1),
        ExpiresAt = DateTime.UtcNow.AddDays(1),
        Questions = [new QuestionItem { QuestionText = "Q" }]
    };

    // ── 1. CREATE — cận dưới ───────────────────────────────────────────────────────────────────

    // `-1` và `0` là hai ca KHÁC nhau về mặt bug: `0` lọt qua vế `is > 0` của nhánh create theo đúng
    // nghĩa đen của nó, còn `-1` lọt qua cả phép so trần của nhánh update. Giữ riêng để một bản vá
    // chỉ chặn số âm (`< 0` thay vì `< 1`) vẫn ĐỎ.
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Create_TranRiengKhongDuong_Nem_VaKhongTaoCampaign(int maxCandidates)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var spy = new SaveSpy();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service(tdb.NewContext(spy), Business).CreateCampaignAsync(org, org, Request(maxCandidates), default));

        // Thông điệp phải nói đúng cái sai (con số HR vừa nhập), không phải "vượt trần 200" — HR đọc
        // câu sau sẽ đi chỉnh gói thay vì chỉnh ô mình vừa gõ.
        Assert.Contains($"{maxCandidates}", ex.Message);
        Assert.DoesNotContain("vượt trần", ex.Message);

        Assert.Equal(0, spy.Saves);
        using var read = tdb.NewContext();
        Assert.Empty(await read.Campaigns.ToListAsync());
    }

    // Cận dưới là 1, không phải 2 — off-by-one ở đây cấm mất một cấu hình HỢP LỆ (chiến dịch mời đúng
    // một người), mà lỗi kiểu "chặn nhầm cái đúng" không ai báo vì HR chỉ việc nhập số khác.
    [Fact]
    public async Task Create_TranRiengBangMot_Qua_VaLuuDungGiaTri()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var spy = new SaveSpy();

        var created = await Service(tdb.NewContext(spy), Business).CreateCampaignAsync(org, org, Request(1), default);

        Assert.Equal(1, created.MaxCandidates);
        using var read = tdb.NewContext();
        Assert.Equal(1, (await read.Campaigns.SingleAsync(c => c.Id == created.Id)).MaxCandidates);

        // ĐỐI CHỨNG DƯƠNG cho `SaveSpy`, dùng ĐÚNG cách đấu dây của các ca âm. Một `Saves == 0` đứng
        // một mình là đồng hồ chết: nó cũng đúng khi interceptor không hề được gắn vào DbContext.
        Assert.True(spy.Saves > 0, "SaveSpy phải đếm được lượt ghi ở đường THÀNH CÔNG, nếu không thì Saves == 0 ở các ca âm không chứng minh gì.");
    }

    [Fact]
    public async Task Create_KhongDatTranRieng_Qua_VaLuuNull()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var created = await Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(null), default);

        Assert.Null(created.MaxCandidates);
        using var read = tdb.NewContext();
        Assert.Null((await read.Campaigns.SingleAsync(c => c.Id == created.Id)).MaxCandidates);
    }

    // ── 2. UPDATE — cận dưới (đường ghi thứ hai, hỏng độc lập) ─────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Update_TranRiengKhongDuong_Nem_VaKhongDoiGiaTriDaLuu(int maxCandidates)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var created = await Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(5), default);

        var spy = new SaveSpy();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service(tdb.NewContext(spy), Business).UpdateCampaignAsync(
                org, org, created.Id, new UpdateCampaignRequest { MaxCandidates = maxCandidates }, default));

        Assert.Contains($"{maxCandidates}", ex.Message);
        Assert.Equal(0, spy.Saves);

        // Vế quyết định: trần CŨ còn nguyên. Nếu guard chạy SAU dòng gán thì entity đã mang -1 và chỉ
        // còn phụ thuộc vào việc không ai gọi SaveChanges — một sự an toàn do tình cờ.
        using var read = tdb.NewContext();
        Assert.Equal(5, (await read.Campaigns.SingleAsync(c => c.Id == created.Id)).MaxCandidates);
    }

    [Fact]
    public async Task Update_TranRiengBangMot_Qua()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var created = await Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(5), default);

        var updated = await Service(tdb.NewContext(), Business).UpdateCampaignAsync(
            org, org, created.Id, new UpdateCampaignRequest { MaxCandidates = 1 }, default);

        Assert.Equal(1, updated.MaxCandidates);
        using var read = tdb.NewContext();
        Assert.Equal(1, (await read.Campaigns.SingleAsync(c => c.Id == created.Id)).MaxCandidates);
    }

    // `null` trên PUT = "không đổi" (merge-only-if-provided), KHÔNG phải "xoá trần". Guard phải trả
    // sớm ở null, nếu không thì mọi lần HR bấm Lưu mà không đụng ô này đều ăn 400.
    [Fact]
    public async Task Update_KhongGuiTranRieng_KhongNem_VaGiuNguyenTranCu()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var created = await Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(5), default);

        var updated = await Service(tdb.NewContext(), Business).UpdateCampaignAsync(
            org, org, created.Id, new UpdateCampaignRequest { Title = "Đổi tên thôi" }, default);

        Assert.Equal(5, updated.MaxCandidates);
        using var read = tdb.NewContext();
        Assert.Equal(5, (await read.Campaigns.SingleAsync(c => c.Id == created.Id)).MaxCandidates);
    }

    // ── 3. ĐỐI CHỨNG DƯƠNG — đi hết đường tới lời mời ──────────────────────────────────────────

    /// <summary>
    /// Không có ca này thì mọi "400" ở trên có thể xanh vì một lý do khác hẳn (request hỏng shape,
    /// entitlement dựng sai, campaign không tạo được vì lý do nào đó) — và quan trọng hơn: chính
    /// <b>đường mời</b> mới là nơi bug biểu hiện. Trần <c>1</c> phải mời được ĐÚNG một người rồi mới chặn;
    /// dưới bug thì <c>1</c> vẫn hợp lệ nên ca này không phân biệt được, nhưng nó khoá chiều ngược lại:
    /// một bản vá quá tay (ví dụ cận dưới đặt ở 2) sẽ làm cấu hình hợp lệ này chết.
    /// </summary>
    [Fact]
    public async Task TranRiengBangMot_MoiDuocDungMotNguoi_NguoiThuHaiBiChan()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var created = await Service(tdb.NewContext(), Business).CreateCampaignAsync(org, org, Request(1), default);

        using (var open = tdb.NewContext())
        {
            (await open.Campaigns.SingleAsync(c => c.Id == created.Id)).Status = CampaignStatus.Active;
            await open.SaveChangesAsync();
        }

        var svc = Service(tdb.NewContext(), Business);
        var first = await svc.CreateInvitationsAsync(org, org, created.Id, ["a@example.com"], default);
        Assert.Single(first.Created);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateInvitationsAsync(org, org, created.Id, ["b@example.com"], default));
        Assert.Contains("hiện có 1", ex.Message);

        using var read = tdb.NewContext();
        Assert.Single(await read.CampaignInvitations.Where(i => i.CampaignId == created.Id).ToListAsync());
    }

    // ── 4. `null` vẫn nghĩa "không đặt trần RIÊNG" — đo END-TO-END ─────────────────────────────

    /// <summary>
    /// Vế KHÔNG hiển nhiên của bản vá, và là rủi ro hồi quy thật: một guard hăng tay (<c>[Required]</c>,
    /// hoặc <c>null → 0</c> rồi mới kiểm) sẽ chặn nhầm đúng ca MẶC ĐỊNH — HR để trống ô trần.
    ///
    /// <para>Chỗ này cố ý KHÔNG assert "<c>Validate(null, …)</c> không ném": vế đó đúng một cách tầm
    /// thường và vẫn xanh kể cả khi <c>null</c> bị diễn giải sai ở tầng dưới. Phép đo có nghĩa là đi hết
    /// đường: campaign để trống trần phải mời được người tới ĐÚNG trần của GÓI (ở đây 2), rồi mới chặn.
    /// Nó khoá luôn ranh giới <c>Math.Min(campaignCap, planCap)</c> — chính chỗ mà <c>-5</c> đã ký sinh
    /// để biến trần thành số âm.</para>
    /// </summary>
    [Fact]
    public async Task KhongDatTranRieng_VanMoiDuocToiDungTranGoi()
    {
        var plan = new CampaignEntitlement("resolved", "nho", 1, 10, MaxCandidatesCap: 2, true, true, true);
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var created = await Service(tdb.NewContext(), plan).CreateCampaignAsync(org, org, Request(null), default);
        Assert.Null(created.MaxCandidates);   // tiền đề: trần RIÊNG thật sự để trống

        using (var open = tdb.NewContext())
        {
            (await open.Campaigns.SingleAsync(c => c.Id == created.Id)).Status = CampaignStatus.Active;
            await open.SaveChangesAsync();
        }

        var svc = Service(tdb.NewContext(), plan);
        var ok = await svc.CreateInvitationsAsync(org, org, created.Id, ["a@example.com", "b@example.com"], default);
        Assert.Equal(2, ok.Created.Count);
        Assert.Empty(ok.Failed);

        // Người thứ 3 vượt trần GÓI ⇒ chặn. "hiện có 2" chứng minh trần hiệu lực đúng là 2 (trần gói),
        // không phải 0 hay một số âm nào đó do `null` bị quy về số.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateInvitationsAsync(org, org, created.Id, ["c@example.com"], default));
        Assert.Contains("hiện có 2", ex.Message);

        using var read = tdb.NewContext();
        Assert.Equal(2, await read.CampaignInvitations.CountAsync(i => i.CampaignId == created.Id));
    }

    // ── 5. Bất biến `MaxCandidatesCap >= 1` mà cận TRÊN dựa vào ────────────────────────────────

    /// <summary>
    /// <see cref="MaxCandidatesRule"/> ghi rằng cận trên chỉ chạy sau khi cận dưới đã lọc, dựa trên
    /// bất biến <c>MaxCandidatesCap ≥ 1</c>. Bất biến đó do BA nguồn giữ: hai fallback hằng số, và
    /// <see cref="EntitlementClient"/> từ chối snapshot khai cap &lt; 1. Vế thứ ba <b>chưa test nào
    /// phủ</b> — mà nó mới là nguồn duy nhất nhận số từ bên ngoài (Payment). Mất nó thì trần hiệu lực
    /// có thể âm trở lại, lần này qua cửa gói dịch vụ thay vì cửa HR nhập.
    /// </summary>
    [Fact]
    public async Task GoiKhaiTranUngVienDuoiMot_BiTuChoi_RoiVeStarter()
    {
        var snapshot = """{"MaxActiveCampaigns":5,"MaxCandidatesCap":0,"AdaptiveEnabled":true,"GroundingEnabled":true,"PostpaidEligible":true}""";
        var body = $$"""{"source":"resolved","tierCode":"business","tierRank":1,"entitlementSnapshot":{{System.Text.Json.JsonSerializer.Serialize(snapshot)}}}""";

        using var client = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://payment.test") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new EntitlementClient(client, Mock.Of<IConfiguration>(), cache,
            NullLogger<EntitlementClient>.Instance, Options.Create(new TieringSettings { Enabled = true }));

        var entitlement = await sut.ResolveOrgAsync(Guid.NewGuid());

        Assert.Equal(CampaignEntitlement.Starter, entitlement);
        Assert.True(entitlement.MaxCandidatesCap >= MaxCandidatesRule.MinCandidates);
    }

    // Đối chứng dương cho ca trên: cùng handler, cap HỢP LỆ thì snapshot được nhận thật. Thiếu vế này,
    // "rơi về Starter" có thể chỉ vì handler/JSON dựng sai chứ không vì cap < 1.
    [Fact]
    public async Task GoiKhaiTranUngVienHopLe_DuocNhan()
    {
        var snapshot = """{"MaxActiveCampaigns":5,"MaxCandidatesCap":1,"AdaptiveEnabled":true,"GroundingEnabled":true,"PostpaidEligible":true}""";
        var body = $$"""{"source":"resolved","tierCode":"business","tierRank":1,"entitlementSnapshot":{{System.Text.Json.JsonSerializer.Serialize(snapshot)}}}""";

        using var client = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://payment.test") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new EntitlementClient(client, Mock.Of<IConfiguration>(), cache,
            NullLogger<EntitlementClient>.Instance, Options.Create(new TieringSettings { Enabled = true }));

        var entitlement = await sut.ResolveOrgAsync(Guid.NewGuid());

        Assert.Equal("business", entitlement.TierCode);
        Assert.Equal(1, entitlement.MaxCandidatesCap);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
