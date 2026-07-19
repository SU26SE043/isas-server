using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// F17 — vòng đời API key bên thứ ba: tạo (key thô 1 lần, DB chỉ giữ hash) · liệt kê (không lộ key)
/// · thu hồi (org-scoped, idempotent) · xác thực (sai/thiếu/revoke/hết hạn → null).
/// </summary>
public class ApiKeyServiceF17Tests
{
    private static ApiKeySettings Settings(Action<ApiKeySettings>? tweak = null)
    {
        var s = new ApiKeySettings();
        tweak?.Invoke(s);
        return s;
    }

    private static ApiKeyService NewService(CampaignDbContext db, ApiKeySettings? settings = null) =>
        new(db, Options.Create(settings ?? Settings()), Mock.Of<ILogger<ApiKeyService>>());

    private static CreateApiKeyRequest Req(string name = "Greenhouse", bool pii = false, int? days = null)
        => new() { Name = name, IncludePii = pii, ExpiresInDays = days };

    // ── Tạo ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_tra_key_tho_va_DB_chi_giu_hash()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();

        var created = await NewService(tdb.NewContext()).CreateAsync(orgId, Guid.NewGuid(), Req(), default);

        Assert.StartsWith(ApiKeys.Prefix, created.Key);

        var row = await tdb.NewContext().ApiKeys.SingleAsync();
        // Bất biến trung tâm của task: key thô KHÔNG nằm trong DB ở bất kỳ cột nào.
        Assert.DoesNotContain(created.Key, row.KeyHash);
        Assert.NotEqual(created.Key, row.KeyHash);
        Assert.Equal(ApiKeys.Hash(created.Key), row.KeyHash);
        Assert.Equal(orgId, row.OrgId);
    }

    [Fact]
    public async Task Create_ghi_audit_va_khong_log_key_tho()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var actor = Guid.NewGuid();

        var created = await NewService(tdb.NewContext()).CreateAsync(orgId, actor, Req("ATS"), default);

        var audit = await tdb.NewContext().AuditLogs.SingleAsync();
        Assert.Equal(AuditAction.CreateApiKey, audit.Action);
        Assert.Equal(orgId, audit.OrgId);
        Assert.Equal(actor, audit.ActorUserId);
        // audit_logs đọc được rộng hơn api_keys → không được chứa key thô lẫn hash.
        Assert.DoesNotContain(created.Key, audit.Summary);
        Assert.DoesNotContain(ApiKeys.Hash(created.Key), audit.Summary);
    }

    [Fact]
    public async Task Create_hai_lan_ra_hai_key_khac_nhau()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var svc = NewService(tdb.NewContext());

        var a = await svc.CreateAsync(orgId, Guid.NewGuid(), Req("a"), default);
        var b = await svc.CreateAsync(orgId, Guid.NewGuid(), Req("b"), default);

        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public async Task Create_luon_co_han_va_chan_han_vuot_tran()
    {
        using var tdb = new CampaignTestDb();
        var settings = Settings(s => { s.DefaultExpiryDays = 30; s.MaxExpiryDays = 90; });

        var created = await NewService(tdb.NewContext(), settings)
            .CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Req(), default);
        // DB23: hạn KHÔNG được vô hạn.
        Assert.True(created.ExpiresAt > DateTime.UtcNow.AddDays(29));
        Assert.True(created.ExpiresAt < DateTime.UtcNow.AddDays(31));

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService(tdb.NewContext(), settings)
            .CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Req(days: 91), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService(tdb.NewContext(), settings)
            .CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Req(days: 0), default));
    }

    [Fact]
    public async Task Create_chan_khi_org_dat_tran_key_active()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var settings = Settings(s => s.MaxActiveKeysPerOrg = 2);

        await NewService(tdb.NewContext(), settings).CreateAsync(orgId, Guid.NewGuid(), Req("1"), default);
        await NewService(tdb.NewContext(), settings).CreateAsync(orgId, Guid.NewGuid(), Req("2"), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService(tdb.NewContext(), settings)
            .CreateAsync(orgId, Guid.NewGuid(), Req("3"), default));

        // Org KHÁC không bị ảnh hưởng bởi trần của org này.
        await NewService(tdb.NewContext(), settings)
            .CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Req("khac-org"), default);
    }

    // ── Xác thực ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Authenticate_key_dung_tra_dung_org()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var created = await NewService(tdb.NewContext()).CreateAsync(orgId, Guid.NewGuid(), Req(pii: true), default);

        var principal = await NewService(tdb.NewContext()).AuthenticateAsync(created.Key, default);

        Assert.NotNull(principal);
        Assert.Equal(orgId, principal!.OrgId);
        Assert.Equal(created.Id, principal.KeyId);
        Assert.True(principal.IncludePii);
    }

    // Vế ÂM của cờ PII — thiếu nó thì bộ test có một lỗ THẬT trên đường dữ liệu cá nhân.
    // Ca dương ở trên chỉ assert `Assert.True(principal.IncludePii)`, nên một lỗi biến MỌI key thành
    // được-đọc-PII (vd hardcode `true` lúc dựng principal) vẫn thoả mãn nó một cách tầm thường: mutation
    // đúng chỗ đó cho 406/406 XANH. `PublicApiF17Tests` không đỡ được ca này vì nó dựng claim BẰNG TAY,
    // không đi qua AuthenticateAsync ⇒ mapping api_keys.include_pii → principal chỉ được khoá ở đây.
    [Fact]
    public async Task Authenticate_key_khong_bat_pii_thi_principal_KHONG_duoc_mo_pii()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var created = await NewService(tdb.NewContext()).CreateAsync(orgId, Guid.NewGuid(), Req(pii: false), default);

        var principal = await NewService(tdb.NewContext()).AuthenticateAsync(created.Key, default);

        Assert.NotNull(principal);
        Assert.False(principal!.IncludePii);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("khong-phai-key")]
    [InlineData("isas_ak_saibet")]
    public async Task Authenticate_key_sai_hoac_thieu_tra_null(string? bad)
    {
        using var tdb = new CampaignTestDb();
        await NewService(tdb.NewContext()).CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Req(), default);

        Assert.Null(await NewService(tdb.NewContext()).AuthenticateAsync(bad, default));
    }

    [Fact]
    public async Task Authenticate_key_da_thu_hoi_tra_null()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var created = await NewService(tdb.NewContext()).CreateAsync(orgId, Guid.NewGuid(), Req(), default);
        Assert.NotNull(await NewService(tdb.NewContext()).AuthenticateAsync(created.Key, default));

        await NewService(tdb.NewContext()).RevokeAsync(orgId, Guid.NewGuid(), created.Id, default);

        Assert.Null(await NewService(tdb.NewContext()).AuthenticateAsync(created.Key, default));
    }

    [Fact]
    public async Task Authenticate_key_het_han_tra_null()
    {
        using var tdb = new CampaignTestDb();
        var created = await NewService(tdb.NewContext()).CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Req(), default);

        // Đẩy hạn về quá khứ.
        await tdb.NewContext().ApiKeys
            .Where(k => k.Id == created.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));

        Assert.Null(await NewService(tdb.NewContext()).AuthenticateAsync(created.Key, default));
    }

    [Fact]
    public async Task Authenticate_ghi_last_used_at_va_tiet_che_lan_ke()
    {
        using var tdb = new CampaignTestDb();
        var created = await NewService(tdb.NewContext())
            .CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Req(), default);

        await NewService(tdb.NewContext()).AuthenticateAsync(created.Key, default);
        var first = (await tdb.NewContext().ApiKeys.SingleAsync()).LastUsedAt;
        Assert.NotNull(first);

        // Gọi lại ngay → còn trong cửa sổ tiết chế → KHÔNG ghi lại (tránh 1 UPDATE mỗi request).
        await NewService(tdb.NewContext()).AuthenticateAsync(created.Key, default);
        Assert.Equal(first, (await tdb.NewContext().ApiKeys.SingleAsync()).LastUsedAt);

        // Tiết chế = 0 phút → ghi mỗi lần.
        await NewService(tdb.NewContext(), Settings(s => s.TouchThrottleMinutes = 0))
            .AuthenticateAsync(created.Key, default);
        Assert.True((await tdb.NewContext().ApiKeys.SingleAsync()).LastUsedAt >= first);
    }

    // ── Liệt kê / thu hồi (org scope) ────────────────────────────────────

    [Fact]
    public async Task List_chi_tra_key_cua_org_minh_va_khong_lo_key()
    {
        using var tdb = new CampaignTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var a = await NewService(tdb.NewContext()).CreateAsync(orgA, Guid.NewGuid(), Req("cua-A"), default);
        await NewService(tdb.NewContext()).CreateAsync(orgB, Guid.NewGuid(), Req("cua-B"), default);

        var listA = await NewService(tdb.NewContext()).ListAsync(orgA, default);

        var only = Assert.Single(listA);
        Assert.Equal("cua-A", only.Name);
        Assert.True(only.IsActive);
        // ApiKeyResponse không có trường nào mang key thô/hash — khoá bằng phản chiếu để lần sau
        // thêm field vào DTO mà lỡ tay lộ hash thì test này đỏ.
        var props = typeof(ApiKeyResponse).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Key", props);
        Assert.DoesNotContain("KeyHash", props);
        Assert.Equal(ApiKeys.DisplayPrefix(a.Key), only.KeyPrefix);
    }

    [Fact]
    public async Task Revoke_key_cua_org_khac_thi_404_va_key_do_van_song()
    {
        using var tdb = new CampaignTestDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var keyB = await NewService(tdb.NewContext()).CreateAsync(orgB, Guid.NewGuid(), Req(), default);

        // Org A thử thu hồi key của org B → 404 (không phân biệt "của org khác" với "không tồn tại").
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewService(tdb.NewContext()).RevokeAsync(orgA, Guid.NewGuid(), keyB.Id, default));

        // …và key B vẫn dùng được.
        Assert.NotNull(await NewService(tdb.NewContext()).AuthenticateAsync(keyB.Key, default));
    }

    [Fact]
    public async Task Revoke_idempotent_va_ghi_audit()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var created = await NewService(tdb.NewContext()).CreateAsync(orgId, Guid.NewGuid(), Req(), default);

        await NewService(tdb.NewContext()).RevokeAsync(orgId, Guid.NewGuid(), created.Id, default);
        var firstRevokedAt = (await tdb.NewContext().ApiKeys.SingleAsync()).RevokedAt;

        // Gọi lại không ném và không dời mốc thu hồi.
        await NewService(tdb.NewContext()).RevokeAsync(orgId, Guid.NewGuid(), created.Id, default);
        Assert.Equal(firstRevokedAt, (await tdb.NewContext().ApiKeys.SingleAsync()).RevokedAt);

        Assert.Single(await tdb.NewContext().AuditLogs
            .Where(a => a.Action == AuditAction.RevokeApiKey).ToListAsync());
    }

    [Fact]
    public void Hash_deterministic_va_prefix_hien_thi_khong_du_de_doan_key()
    {
        var raw = ApiKeys.NewRawKey();

        Assert.Equal(ApiKeys.Hash(raw), ApiKeys.Hash(raw));
        Assert.NotEqual(ApiKeys.Hash(raw), ApiKeys.Hash(ApiKeys.NewRawKey()));

        var prefix = ApiKeys.DisplayPrefix(raw);
        Assert.Equal(ApiKeys.DisplayPrefixLength, prefix.Length);
        // Phần bí mật còn lại vẫn dài (≈43-6 ký tự base64url ≈ 220 bit) → prefix hiển thị vô hại.
        Assert.True(raw.Length - ApiKeys.Prefix.Length - prefix.Length >= 30);
    }
}
