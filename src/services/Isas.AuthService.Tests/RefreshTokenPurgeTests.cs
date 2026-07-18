using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Tests;

/// <summary>
/// DB28 — job dọn <c>refresh_tokens</c>. Đây là code XOÁ DỮ LIỆU nên test tập trung vào cái KHÔNG
/// được xoá, không chỉ cái được xoá:
///   • token còn sống (chưa hết hạn, chưa thu hồi) phải sống sót bất kể già cỡ nào;
///   • token vừa bị XOAY VÒNG còn trong cửa sổ ân hạn AUTH-9 phải sống sót (xoá = đá người dùng
///     nhiều tab về trang đăng nhập);
///   • mắt xích <c>replaced_by</c> không được đứt (successor không thể mất trước predecessor);
///   • cấu hình ngưỡng giữ sai tay (0 / nhỏ hơn ân hạn) không được biến job thành máy huỷ phiên.
/// </summary>
public class RefreshTokenPurgeTests
{
    private const int GraceSeconds = 60;   // mặc định AUTH-9

    private static RefreshTokenRetentionSettings Settings(
        int retentionDays = 30, int batchSize = 5000, int maxBatches = 20) => new()
        {
            Enabled = true,
            RetentionDays = retentionDays,
            BatchSize = batchSize,
            MaxBatchesPerRun = maxBatches
        };

    // ── Xoá đúng thứ đã chết ────────────────────────────────────────────────

    [Fact]
    public async Task Purge_XoaToken_HetHanTuLau()
    {
        using var t = new AuthTestDb();
        var user = SeedUser(t.Db);
        // Hết hạn 40 ngày trước → RefreshTokenAsync trả 401 "expired" dù có xuất trình.
        SeedToken(t.Db, user.Id, "het-han", createdDaysAgo: 47, expiresDaysAgo: 40);

        var deleted = await RefreshTokenPurge.PurgeAsync(t.Db, DateTime.UtcNow, Settings(), GraceSeconds);

        Assert.Equal(1, deleted);
        Assert.Empty(await t.Db.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task Purge_XoaToken_DaThuHoiTuLau_DuChuaHetHan()
    {
        using var t = new AuthTestDb();
        var user = SeedUser(t.Db);
        // Ca thu hồi THẲNG TAY (đăng xuất / đổi org-role): is_revoked=true, replaced_by NULL, nhưng
        // expires_at vẫn ở tương lai → vế "hết hạn" không dọn nổi. Không bao giờ dùng lại được nữa.
        SeedToken(t.Db, user.Id, "da-dang-xuat",
            createdDaysAgo: 60, expiresInDays: 5, isRevoked: true);

        var deleted = await RefreshTokenPurge.PurgeAsync(t.Db, DateTime.UtcNow, Settings(), GraceSeconds);

        Assert.Equal(1, deleted);
        Assert.Empty(await t.Db.RefreshTokens.ToListAsync());
    }

    // ── KHÔNG được xoá (mutation targets) ───────────────────────────────────

    [Fact]
    public async Task Purge_GiuLai_TokenConSong()
    {
        using var t = new AuthTestDb();
        var user = SeedUser(t.Db);
        // Chưa hết hạn + chưa thu hồi = phiên đang dùng. Tạo cách đây 6 ngày (già hơn nhiều ngưỡng
        // nhỏ) để chắc chắn không có ai lỡ tay lọc theo created_at đơn thuần.
        SeedToken(t.Db, user.Id, "dang-dung", createdDaysAgo: 6, expiresInDays: 1);

        var deleted = await RefreshTokenPurge.PurgeAsync(t.Db, DateTime.UtcNow, Settings(), GraceSeconds);

        Assert.Equal(0, deleted);
        Assert.Single(await t.Db.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task Purge_GiuLai_TokenVuaBiXoayVong_TrongCuaSoAnHan()
    {
        using var t = new AuthTestDb();
        var user = SeedUser(t.Db);
        // AUTH-9: T1 vừa bị xoay 5 giây trước, T2 là token thay thế. Tab đến muộn còn cầm T1 và
        // PHẢI đổi được token. Xoá T1 hoặc T2 ở đây = đá người dùng nhiều tab ra ngoài.
        var (t1, t2) = SeedRotatedPair(t.Db, user.Id, rotatedSecondsAgo: 5);

        var deleted = await RefreshTokenPurge.PurgeAsync(t.Db, DateTime.UtcNow, Settings(), GraceSeconds);

        Assert.Equal(0, deleted);
        var ids = await t.Db.RefreshTokens.Select(x => x.Id).ToListAsync();
        Assert.Contains(t1, ids);
        Assert.Contains(t2, ids);
    }

    [Fact]
    public async Task Purge_KhongLamDut_ChuoiReplacedBy()
    {
        using var t = new AuthTestDb();
        var user = SeedUser(t.Db);
        // Chuỗi cũ: T1 (rất cũ, đã xoay) → T2 (token thay thế). Bất biến: successor luôn TRẺ hơn
        // predecessor ⇒ không thể có ca "T1 còn sống mà T2 đã bị xoá" (đứt đường ân hạn).
        var (t1, t2) = SeedRotatedPair(t.Db, user.Id, rotatedSecondsAgo: (int)TimeSpan.FromDays(45).TotalSeconds);

        await RefreshTokenPurge.PurgeAsync(t.Db, DateTime.UtcNow, Settings(), GraceSeconds);

        var ids = await t.Db.RefreshTokens.Select(x => x.Id).ToListAsync();
        var t1Sống = ids.Contains(t1);
        var t2Sống = ids.Contains(t2);
        Assert.False(t1Sống && !t2Sống);   // predecessor sống mà successor mất = chuỗi đứt
    }

    // ── Cấu hình sai tay không được gây hại ─────────────────────────────────

    [Fact]
    public async Task Purge_NgưỡngGiữ_BiKepSan_KhiCauHinh0()
    {
        using var t = new AuthTestDb();
        var user = SeedUser(t.Db);
        // RetentionDays=0 nếu dùng thẳng thì cutoff = now → xoá sạch mọi token đã thu hồi, kể cả
        // token vừa xoay 10 phút trước. Sàn cứng phải chặn ca này.
        var (t1, t2) = SeedRotatedPair(t.Db, user.Id, rotatedSecondsAgo: 600);

        var deleted = await RefreshTokenPurge.PurgeAsync(t.Db, DateTime.UtcNow, Settings(retentionDays: 0), GraceSeconds);

        Assert.Equal(0, deleted);
        Assert.Equal(2, await t.Db.RefreshTokens.CountAsync());
        Assert.Equal(RefreshTokenRetentionSettings.DefaultRetentionDays,
            RefreshTokenPurge.ResolveRetentionDays(Settings(retentionDays: 0), GraceSeconds));
        _ = (t1, t2);
    }

    [Fact]
    public void ResolveRetentionDays_NangNguongGiu_LenTrenCuaSoAnHan()
    {
        // Ân hạn đặt lớn bất thường (10 ngày) mà ngưỡng giữ chỉ 2 ngày → giữ theo ngưỡng cấu hình sẽ
        // xoá đúng những token đang được ân hạn. Ngưỡng phải tự nâng lên trên cửa sổ đó.
        var days = RefreshTokenPurge.ResolveRetentionDays(
            Settings(retentionDays: 2), graceSeconds: (int)TimeSpan.FromDays(10).TotalSeconds);

        Assert.True(days > 10, $"ngưỡng giữ {days} ngày không được nhỏ hơn cửa sổ ân hạn 10 ngày");
    }

    [Fact]
    public async Task Purge_AnHanLon_KhongXoaTokenDangDuocAnHan()
    {
        using var t = new AuthTestDb();
        var user = SeedUser(t.Db);
        // Ân hạn 10 ngày + ngưỡng giữ cấu hình 2 ngày; token xoay 3 ngày trước vẫn trong ân hạn.
        var grace = (int)TimeSpan.FromDays(10).TotalSeconds;
        SeedRotatedPair(t.Db, user.Id, rotatedSecondsAgo: (int)TimeSpan.FromDays(3).TotalSeconds);

        var deleted = await RefreshTokenPurge.PurgeAsync(
            t.Db, DateTime.UtcNow, Settings(retentionDays: 2), grace);

        Assert.Equal(0, deleted);
        Assert.Equal(2, await t.Db.RefreshTokens.CountAsync());
    }

    // ── Vận hành ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_TonTrongTranBatch_MoiVong()
    {
        using var t = new AuthTestDb();
        var user = SeedUser(t.Db);
        for (var i = 0; i < 10; i++)
            SeedToken(t.Db, user.Id, $"cu-{i}", createdDaysAgo: 50, expiresDaysAgo: 43);

        // batch 2 × tối đa 3 vòng = xoá nhiều nhất 6, phần còn lại để vòng quét sau.
        var deleted = await RefreshTokenPurge.PurgeAsync(
            t.Db, DateTime.UtcNow, Settings(batchSize: 2, maxBatches: 3), GraceSeconds);

        Assert.Equal(6, deleted);
        Assert.Equal(4, await t.Db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task Purge_BangRong_KhongLoi_TraVe0()
    {
        using var t = new AuthTestDb();

        var deleted = await RefreshTokenPurge.PurgeAsync(t.Db, DateTime.UtcNow, Settings(), GraceSeconds);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task Purge_ChiDonRacCuaChinhBang_KhongDungUser()
    {
        using var t = new AuthTestDb();
        var user = SeedUser(t.Db);
        SeedToken(t.Db, user.Id, "rac", createdDaysAgo: 50, expiresDaysAgo: 43);

        await RefreshTokenPurge.PurgeAsync(t.Db, DateTime.UtcNow, Settings(), GraceSeconds);

        Assert.Equal(1, await t.Db.Users.CountAsync());   // xoá token KHÔNG được kéo theo tài khoản
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static User SeedUser(AuthDbContext db)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = "purge@acme.test",
            Email = "purge@acme.test",
            FullName = "Purge Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static Guid SeedToken(
        AuthDbContext db, Guid userId, string token,
        int createdDaysAgo, int? expiresDaysAgo = null, int? expiresInDays = null,
        bool isRevoked = false, Guid? replacedBy = null)
    {
        var id = Guid.NewGuid();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = id,
            UserId = userId,
            Token = token,
            IsRevoked = isRevoked,
            ReplacedBy = replacedBy,
            CreatedAt = DateTime.UtcNow.AddDays(-createdDaysAgo),
            ExpiresAt = expiresDaysAgo is int d
                ? DateTime.UtcNow.AddDays(-d)
                : DateTime.UtcNow.AddDays(expiresInDays ?? 7)
        });
        db.SaveChanges();
        return id;
    }

    /// <summary>
    /// Cặp token đúng hình dạng rotation thật: T1 bị thu hồi và trỏ <c>replaced_by</c> sang T2, T2 tạo
    /// đúng thời điểm xoay và nhận hạn mới 7 ngày kể từ đó (mirror <c>RefreshTokenAsync</c>).
    /// </summary>
    private static (Guid T1, Guid T2) SeedRotatedPair(AuthDbContext db, Guid userId, int rotatedSecondsAgo)
    {
        var rotatedAt = DateTime.UtcNow.AddSeconds(-rotatedSecondsAgo);

        var t2 = Guid.NewGuid();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = t2,
            UserId = userId,
            Token = $"thay-the-{t2}",
            IsRevoked = false,
            CreatedAt = rotatedAt,
            ExpiresAt = rotatedAt.AddDays(7)
        });

        var t1 = Guid.NewGuid();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = t1,
            UserId = userId,
            Token = $"bi-xoay-{t1}",
            IsRevoked = true,
            ReplacedBy = t2,
            CreatedAt = rotatedAt.AddDays(-1),           // token cũ luôn già hơn token thay thế
            ExpiresAt = rotatedAt.AddDays(-1).AddDays(7)
        });

        db.SaveChanges();
        return (t1, t2);
    }
}
