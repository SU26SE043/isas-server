using System.Text.Json;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

// 'CampaignService' vừa là namespace vừa là tên class → alias cho rõ ràng.
using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// DB23 — token magic-link lưu BĂM, không lưu thô + `expires_at` luôn có giá trị.
/// Bảo vệ 2 tính chất: (1) đọc được DB ≠ mạo danh được invitee (DB chỉ có hash);
/// (2) không còn token sống vĩnh viễn (campaign không deadline → hạn mặc định).
/// </summary>
public class InvitationTokenHashDb23Tests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static CampaignSvc NewService(CampaignDbContext db, int? defaultExpiryDays = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(), null,
            defaultExpiryDays is int d
                ? Options.Create(new InvitationSettings { DefaultExpiryDays = d })
                : null);

    private static ParticipationService NewParticipation(CampaignDbContext db) =>
        new(db, Mock.Of<IAuthProvisionClient>(), Mock.Of<ICampaignSessionClient>(),
            NullLogger<ParticipationService>.Instance);

    // Token THÔ chỉ tồn tại trên đường tới email = outbox payload (DB2b).
    private static async Task<string> RawTokenFromOutbox(CampaignDbContext db)
    {
        var row = await db.OutboxMessages.SingleAsync();
        return JsonSerializer.Deserialize<InvitationEmailJob>(row.Payload, JsonOptions)!.Token;
    }

    private static async Task<(Guid CampaignId, string RawToken)> InviteOne(
        CampaignTestDb tdb, Guid owner, DateTime? campaignExpiresAt = null, int? defaultExpiryDays = null)
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.ExpiresAt = campaignExpiresAt;
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        await NewService(tdb.NewContext(), defaultExpiryDays)
            .CreateInvitationsAsync(owner, owner, camp.Id, new List<string> { "cand@example.com" }, default);

        using var read = tdb.NewContext();
        return (camp.Id, await RawTokenFromOutbox(read));
    }

    // (a) 🔴 Tính chất cốt lõi: DB KHÔNG chứa token thô — chỉ chứa SHA-256 của nó.
    [Fact]
    public async Task Tao_LoiMoi_DB_Luu_Hash_Khong_Luu_Token_Tho()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (campaignId, rawToken) = await InviteOne(tdb, owner);

        using var check = tdb.NewContext();
        var inv = await check.CampaignInvitations.SingleAsync(i => i.CampaignId == campaignId);

        Assert.NotEqual(rawToken, inv.TokenHash);                       // KHÔNG lưu thô
        Assert.Equal(InvitationTokens.Hash(rawToken), inv.TokenHash);   // đúng hash của token đã gửi
        Assert.DoesNotContain(rawToken, inv.TokenHash);                 // không nhúng bản thô vào chuỗi
    }

    // (b) Round-trip thật: token thô trong email redeem được (hash lookup khớp).
    [Fact]
    public async Task Token_Tho_Tu_Email_Van_Redeem_Duoc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (campaignId, rawToken) = await InviteOne(tdb, owner);

        var meta = await NewParticipation(tdb.NewContext()).GetInvitationMetadataAsync(rawToken, default);
        Assert.Equal(campaignId, meta.CampaignId);
    }

    // (c) Cầm HASH (kịch bản kẻ đọc được DB/backup) → KHÔNG vào được: 404 như token lạ.
    [Fact]
    public async Task Cam_Hash_Doc_Tu_DB_Khong_Redeem_Duoc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (campaignId, _) = await InviteOne(tdb, owner);

        using var read = tdb.NewContext();
        var storedHash = (await read.CampaignInvitations.SingleAsync(i => i.CampaignId == campaignId)).TokenHash;

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewParticipation(tdb.NewContext()).GetInvitationMetadataAsync(storedHash, default));
    }

    // (d) Token rỗng/trắng → 404 (KeyNotFoundException), KHÔNG để Hash() ném ArgumentException = 500.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Token_Rong_Tra_404_Khong_500(string token)
    {
        using var tdb = new CampaignTestDb();
        var svc = NewParticipation(tdb.NewContext());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetInvitationMetadataAsync(token, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.JoinCampaignAsync(token, null, default));
    }

    // (e) Copy link từ email dính khoảng trắng → vẫn redeem được (trim trước khi băm).
    [Fact]
    public async Task Token_Co_Khoang_Trang_Thua_Van_Redeem_Duoc()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (campaignId, rawToken) = await InviteOne(tdb, owner);

        var meta = await NewParticipation(tdb.NewContext())
            .GetInvitationMetadataAsync($"  {rawToken}\n", default);
        Assert.Equal(campaignId, meta.CampaignId);
    }

    // (f) 🔴 Campaign KHÔNG có deadline → token vẫn có hạn (trước DB23 là NULL = sống vĩnh viễn).
    [Fact]
    public async Task Campaign_Khong_Deadline_Token_Van_Co_Han_Mac_Dinh()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var before = DateTime.UtcNow;
        var (campaignId, _) = await InviteOne(tdb, owner, campaignExpiresAt: null);

        using var check = tdb.NewContext();
        var inv = await check.CampaignInvitations.SingleAsync(i => i.CampaignId == campaignId);

        Assert.InRange(inv.ExpiresAt, before.AddDays(14), DateTime.UtcNow.AddDays(14).AddMinutes(1));
    }

    // (g) Campaign CÓ deadline → token dùng đúng deadline campaign (token ≤ hạn campaign).
    [Fact]
    public async Task Campaign_Co_Deadline_Token_Theo_Deadline_Campaign()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var deadline = DateTime.UtcNow.AddDays(3);
        var (campaignId, _) = await InviteOne(tdb, owner, campaignExpiresAt: deadline);

        using var check = tdb.NewContext();
        var inv = await check.CampaignInvitations.SingleAsync(i => i.CampaignId == campaignId);

        Assert.Equal(deadline, inv.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    // (h) `Invitation:DefaultExpiryDays` có tác dụng; cấu hình ≤0 bị kẹp lên 1 ngày (không vô hạn).
    [Theory]
    [InlineData(3, 3)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task Cau_Hinh_DefaultExpiryDays_Duoc_Ton_Trong(int configured, int expectedDays)
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var before = DateTime.UtcNow;
        var (campaignId, _) = await InviteOne(tdb, owner, campaignExpiresAt: null, defaultExpiryDays: configured);

        using var check = tdb.NewContext();
        var inv = await check.CampaignInvitations.SingleAsync(i => i.CampaignId == campaignId);

        Assert.InRange(inv.ExpiresAt,
            before.AddDays(expectedDays), DateTime.UtcNow.AddDays(expectedDays).AddMinutes(1));
    }

    // (i) Reissue cũng băm (không hở đường thứ 2) + token mới khác token cũ.
    [Fact]
    public async Task Reissue_Cung_Luu_Hash_Va_Doi_Token()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (campaignId, oldRaw) = await InviteOne(tdb, owner);

        using var seed = tdb.NewContext();
        var oldInv = await seed.CampaignInvitations.SingleAsync(i => i.CampaignId == campaignId);

        var fresh = await NewService(tdb.NewContext())
            .ReissueInvitationAsync(owner, owner, campaignId, oldInv.Id, default);

        using var check = tdb.NewContext();
        var freshInv = await check.CampaignInvitations.SingleAsync(i => i.Id == fresh.Id);
        var freshRow = await check.OutboxMessages.SingleAsync(m => m.InvitationId == fresh.Id);
        var freshRaw = JsonSerializer.Deserialize<InvitationEmailJob>(freshRow.Payload, JsonOptions)!.Token;

        Assert.NotEqual(oldRaw, freshRaw);
        Assert.Equal(InvitationTokens.Hash(freshRaw), freshInv.TokenHash);
        Assert.NotEqual(freshRaw, freshInv.TokenHash);
    }

    // (j) Hash: deterministic, 44 ký tự base64 → vừa varchar(128) nên KHÔNG cần đổi kiểu cột.
    [Fact]
    public void Hash_Deterministic_Va_Vua_Cot_128()
    {
        var raw = InvitationTokens.NewRawToken();

        Assert.Equal(InvitationTokens.Hash(raw), InvitationTokens.Hash(raw));
        Assert.NotEqual(InvitationTokens.Hash(raw), InvitationTokens.Hash(InvitationTokens.NewRawToken()));
        Assert.Equal(44, InvitationTokens.Hash(raw).Length);
        Assert.True(InvitationTokens.Hash(raw).Length <= 128);
    }

    // (k) 🔴 Khoá WIRE FORMAT với migration: backfill DB23 băm token cũ bằng SQL Postgres
    // `encode(sha256(convert_to(token,'UTF8')), 'base64')`. Vector dưới đây là output THẬT của câu SQL
    // đó (verify trên Postgres). Nếu đổi lược đồ băm (thêm salt, đổi encoding, bỏ padding…) mà quên
    // migration thì test này đỏ — nếu không, mọi lời mời đã backfill sẽ chết âm thầm.
    [Theory]
    [InlineData("legacy-raw-token-1", "o5WwMqz5p7gp0an3ySLvEgXvEzntbDkahr70BAMsCIw=")]
    [InlineData("legacy-raw-token-2", "UvG1KVO2wakedBy+rdS/JauKvzYroJ07qupgAMpzkLg=")]
    public void Hash_Khop_Byte_Voi_SQL_Backfill_Trong_Migration(string raw, string expectedFromPostgres)
        => Assert.Equal(expectedFromPostgres, InvitationTokens.Hash(raw));
}
