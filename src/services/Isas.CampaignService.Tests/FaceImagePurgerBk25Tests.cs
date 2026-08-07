using System.Reflection;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BK25/DATA-3 — retention ảnh sinh trắc học. Đây là job XOÁ DỮ LIỆU SINH TRẮC (bằng chứng chống
/// gian lận của một buổi thi), nên test khoá TỪNG điều kiện riêng lẻ chứ không chỉ "chạy không nổ":
///  • ảnh còn trong hạn → GIỮ (nới vị ngữ retention = mất bằng chứng buổi đang diễn ra).
///  • ảnh quá hạn → xoá CẢ object S3 LẪN dòng sổ (thiếu một vế = rác mồ côi hoặc mất dấu vết).
///  • S3 xoá lỗi → GIỮ dòng sổ (thà retry mãi còn hơn mất dấu object vẫn nằm trong S3).
///  • tắt bằng config → không xoá gì; trần batch mỗi vòng được tôn trọng.
///  • ảnh THAM CHIẾU → gỡ con trỏ campaign_membership.reference_image_key, NHƯNG chỉ khi con trỏ
///    còn trỏ đúng object vừa xoá (ứng viên có thể đã enroll lại ảnh mới).
/// </summary>
public class FaceImagePurgerBk25Tests
{
    // ── test double cho S3 ─────────────────────────────────────────────────────────
    private sealed class FakeFileService : IFileService
    {
        public readonly List<string> Deleted = new();
        public string? FailOnKey;

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            if (FailOnKey is not null && path == FailOnKey)
                throw new InvalidOperationException($"S3 down for {path}");
            Deleted.Add(path);
            return Task.CompletedTask;
        }

        public Task<string> UploadAsync(IFormFile file, string path, CancellationToken ct = default)
            => Task.FromResult(path);
        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public string GetUrl(string path) => path;
    }

    private static async Task<int> PurgeOnce(FaceImagePurger p)
    {
        var mi = typeof(FaceImagePurger)
            .GetMethod("PurgeOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<int>)mi.Invoke(p, new object[] { CancellationToken.None })!;
    }

    private static FaceImagePurger Build(
        CampaignTestDb t, FakeFileService files, FaceImageRetentionSettings? settings = null)
    {
        var provider = new ServiceCollection()
            .AddDbContext<CampaignDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention())
            .AddSingleton<IFileService>(files)
            .BuildServiceProvider();

        return new FaceImagePurger(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings ?? new FaceImageRetentionSettings
            {
                Enabled = true,
                RetentionDays = 90,
                BatchSize = 200
            }),
            NullLogger<FaceImagePurger>.Instance);
    }

    private static FaceImage Seed(
        CampaignTestDb t, DateTime capturedAt, FaceImageKind kind = FaceImageKind.Live,
        string? key = null, Guid? campaignId = null, Guid? candidateId = null)
    {
        var row = new FaceImage
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId ?? Guid.NewGuid(),
            CandidateId = candidateId ?? Guid.NewGuid(),
            SessionId = kind == FaceImageKind.Live ? Guid.NewGuid() : null,
            Kind = kind,
            StorageKey = key ?? $"campaigns/x/sessions/y/face-live-{Guid.NewGuid():N}.jpg",
            CapturedAt = capturedAt
        };
        t.Db.FaceImages.Add(row);
        t.Db.SaveChanges();
        return row;
    }

    // MUTATION-CHECK #1 — nới vị ngữ retention (bỏ `x.CapturedAt < cutoff`) thì test này ĐỎ:
    // ảnh vừa chụp của buổi thi ĐANG DIỄN RA sẽ bị xoá mất.
    [Fact]
    public async Task Purge_KhongXoa_AnhConTrongHan()
    {
        using var t = new CampaignTestDb();
        var files = new FakeFileService();
        var moiChup = Seed(t, DateTime.UtcNow.AddDays(-2));

        var deleted = await PurgeOnce(Build(t, files));

        Assert.Equal(0, deleted);
        Assert.Empty(files.Deleted);
        Assert.True(await t.NewContext().FaceImages.AnyAsync(x => x.Id == moiChup.Id));
    }

    // Đường chính — quá hạn thì xoá CẢ HAI VẾ. Chỉ xoá dòng DB mà quên S3 (hoặc ngược lại) đều ĐỎ.
    [Fact]
    public async Task Purge_XoaCa_ObjectS3_LanDongSo_KhiQuaHan()
    {
        using var t = new CampaignTestDb();
        var files = new FakeFileService();
        var rac = Seed(t, DateTime.UtcNow.AddDays(-120), key: "campaigns/a/sessions/b/face-live-old.jpg");
        var giu = Seed(t, DateTime.UtcNow.AddDays(-1));

        var deleted = await PurgeOnce(Build(t, files));

        Assert.Equal(1, deleted);
        Assert.Equal(new[] { "campaigns/a/sessions/b/face-live-old.jpg" }, files.Deleted);   // vế S3
        using var db = t.NewContext();
        Assert.False(await db.FaceImages.AnyAsync(x => x.Id == rac.Id));                     // vế DB
        Assert.True(await db.FaceImages.AnyAsync(x => x.Id == giu.Id));
    }

    // MUTATION-CHECK #2 — gỡ `if (!_options.Enabled) return 0;` thì test này ĐỎ.
    [Fact]
    public async Task Purge_TatBangConfig_KhongXoaGi()
    {
        using var t = new CampaignTestDb();
        var files = new FakeFileService();
        Seed(t, DateTime.UtcNow.AddDays(-365));

        var deleted = await PurgeOnce(Build(t, files, new FaceImageRetentionSettings
        {
            Enabled = false,
            RetentionDays = 90,
            BatchSize = 200
        }));

        Assert.Equal(0, deleted);
        Assert.Empty(files.Deleted);
        Assert.Equal(1, await t.NewContext().FaceImages.CountAsync());
    }

    // Mặc định phải là TẮT — bật nhầm một job xoá dữ liệu sinh trắc là chuyện không đảo ngược được.
    // Nếu ai đó đổi default sang true thì test này ĐỎ (mẫu TEST-09: default chưa test = default trôi).
    [Fact]
    public async Task Purge_MacDinh_La_TAT()
    {
        var mac_dinh = new FaceImageRetentionSettings();
        Assert.False(mac_dinh.Enabled);
        Assert.Equal(90, mac_dinh.RetentionDays);   // CAMP-13/D11

        using var t = new CampaignTestDb();
        var files = new FakeFileService();
        Seed(t, DateTime.UtcNow.AddDays(-365));

        // Dựng purger với options MẶC ĐỊNH (không truyền gì) → không được xoá gì.
        Assert.Equal(0, await PurgeOnce(Build(t, files, mac_dinh)));
        Assert.Equal(1, await t.NewContext().FaceImages.CountAsync());
    }

    // MUTATION-CHECK #3 — gỡ `.Take(batch)` thì test này ĐỎ (xoá cả 5 thay vì 2).
    [Fact]
    public async Task Purge_TonTrong_TranBatch_MoiVong()
    {
        using var t = new CampaignTestDb();
        var files = new FakeFileService();
        for (var i = 0; i < 5; i++)
            Seed(t, DateTime.UtcNow.AddDays(-120 + i));

        var purger = Build(t, files, new FaceImageRetentionSettings
        {
            Enabled = true,
            RetentionDays = 90,
            BatchSize = 2
        });

        Assert.Equal(2, await PurgeOnce(purger));
        Assert.Equal(3, await t.NewContext().FaceImages.CountAsync());
        Assert.Equal(2, files.Deleted.Count);

        Assert.Equal(2, await PurgeOnce(purger));
        Assert.Equal(1, await PurgeOnce(purger));
        Assert.Equal(0, await t.NewContext().FaceImages.CountAsync());
        Assert.Equal(5, files.Deleted.Count);
    }

    // Ranh giới retention đọc từ config, không hardcode 90.
    [Fact]
    public async Task Purge_TonTrong_RetentionDays_TuConfig()
    {
        using var t = new CampaignTestDb();
        var files = new FakeFileService();
        var row = Seed(t, DateTime.UtcNow.AddDays(-30));

        Assert.Equal(0, await PurgeOnce(Build(t, files)));      // 90 ngày → ảnh 30 ngày còn giữ

        var deleted = await PurgeOnce(Build(t, files, new FaceImageRetentionSettings
        {
            Enabled = true,
            RetentionDays = 7,
            BatchSize = 200
        }));

        Assert.Equal(1, deleted);
        Assert.False(await t.NewContext().FaceImages.AnyAsync(x => x.Id == row.Id));
    }

    // MUTATION-CHECK #4 — ĐẢO THỨ TỰ (xoá dòng DB trước, S3 sau) hoặc bỏ `continue` khi S3 lỗi
    // thì test này ĐỎ: dòng sổ biến mất trong khi ảnh khuôn mặt vẫn nằm trong S3 = mồ côi không ai
    // trỏ tới = đúng con bug BK25 sinh ra để diệt.
    [Fact]
    public async Task Purge_S3Loi_GiuDongSo_DeVongSauThuLai()
    {
        using var t = new CampaignTestDb();
        var files = new FakeFileService { FailOnKey = "campaigns/a/sessions/b/hong.jpg" };
        var hong = Seed(t, DateTime.UtcNow.AddDays(-120), key: "campaigns/a/sessions/b/hong.jpg");
        var ok = Seed(t, DateTime.UtcNow.AddDays(-120), key: "campaigns/a/sessions/b/ok.jpg");

        var deleted = await PurgeOnce(Build(t, files));

        // Chỉ ảnh xoá được khỏi S3 mới rời sổ; ảnh lỗi GIỮ NGUYÊN dòng.
        Assert.Equal(1, deleted);
        using var db = t.NewContext();
        Assert.True(await db.FaceImages.AnyAsync(x => x.Id == hong.Id));
        Assert.False(await db.FaceImages.AnyAsync(x => x.Id == ok.Id));

        // Vòng sau S3 hồi phục → dọn nốt (tự lành, không cần can thiệp tay).
        files.FailOnKey = null;
        Assert.Equal(1, await PurgeOnce(Build(t, files)));
        Assert.Equal(0, await t.NewContext().FaceImages.CountAsync());
    }

    // MUTATION-CHECK #5 — gỡ khối gỡ con trỏ (ExecuteUpdate reference_image_key = null) thì test này
    // ĐỎ: DB khẳng định "có ảnh tham chiếu" trong khi object đã bị xoá ⇒ face-check gửi KEY CHẾT
    // sang AIService thay vì đi nhánh thật thà identity_unverified.
    [Fact]
    public async Task Purge_AnhThamChieu_GoLuonConTro_TrenMembership()
    {
        using var t = new CampaignTestDb();
        var files = new FakeFileService();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid());
        t.Db.Campaigns.Add(camp);
        var candidateId = Guid.NewGuid();
        const string refKey = "campaigns/c/candidates/d/face-reference.jpg";
        var mem = CampaignTestDb.NewMembership(camp.Id, candidateId, referenceImageKey: refKey);
        t.Db.CampaignMemberships.Add(mem);
        t.Db.SaveChanges();

        Seed(t, DateTime.UtcNow.AddDays(-120), FaceImageKind.Reference, refKey, camp.Id, candidateId);

        Assert.Equal(1, await PurgeOnce(Build(t, files)));

        using var db = t.NewContext();
        var sau = await db.CampaignMemberships.AsNoTracking().FirstAsync(m => m.Id == mem.Id);
        Assert.Null(sau.ReferenceImageKey);
        Assert.Contains(refKey, files.Deleted);
    }

    // MUTATION-CHECK #6 — gỡ guard `m.ReferenceImageKey == row.StorageKey` thì test này ĐỎ:
    // ứng viên đã enroll lại ảnh MỚI (key khác vì đổi đuôi file) sẽ bị xoá trắng ảnh tham chiếu
    // ĐANG DÙNG chỉ vì bản CŨ tới hạn — tức là gate face-verify hỏng đúng lúc buổi thi đang chạy.
    [Fact]
    public async Task Purge_KhongGoConTro_KhiUngVienDaEnrollLaiAnhKhac()
    {
        using var t = new CampaignTestDb();
        var files = new FakeFileService();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid());
        t.Db.Campaigns.Add(camp);
        var candidateId = Guid.NewGuid();
        const string keyCu = "campaigns/c/candidates/d/face-reference.jpg";
        const string keyMoi = "campaigns/c/candidates/d/face-reference.png";
        var mem = CampaignTestDb.NewMembership(camp.Id, candidateId, referenceImageKey: keyMoi);
        t.Db.CampaignMemberships.Add(mem);
        t.Db.SaveChanges();

        // Chỉ bản CŨ quá hạn; bản mới vừa enroll còn trong hạn.
        Seed(t, DateTime.UtcNow.AddDays(-120), FaceImageKind.Reference, keyCu, camp.Id, candidateId);
        Seed(t, DateTime.UtcNow.AddDays(-1), FaceImageKind.Reference, keyMoi, camp.Id, candidateId);

        Assert.Equal(1, await PurgeOnce(Build(t, files)));

        using var db = t.NewContext();
        var sau = await db.CampaignMemberships.AsNoTracking().FirstAsync(m => m.Id == mem.Id);
        Assert.Equal(keyMoi, sau.ReferenceImageKey);          // con trỏ ĐANG DÙNG còn nguyên
        Assert.Equal(new[] { keyCu }, files.Deleted);         // chỉ bản bị thay thế rời S3
    }

    // MUTATION-CHECK #7 — gỡ `.IgnoreQueryFilters()` thì test này ĐỎ. Membership có query filter
    // soft-delete theo Campaign (DB13); campaign ĐÃ soft-delete lại chính là nhóm cần dọn nhất, nên
    // thiếu IgnoreQueryFilters là con trỏ ở đúng nhóm đó nằm trơ mãi mãi.
    [Fact]
    public async Task Purge_VanGoConTro_KhiCampaignDaSoftDelete()
    {
        using var t = new CampaignTestDb();
        var files = new FakeFileService();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid());
        camp.DeletedAt = DateTime.UtcNow.AddDays(-100);       // CAMP-3 soft-delete
        t.Db.Campaigns.Add(camp);
        var candidateId = Guid.NewGuid();
        const string refKey = "campaigns/deleted/candidates/d/face-reference.jpg";
        var mem = CampaignTestDb.NewMembership(camp.Id, candidateId, referenceImageKey: refKey);
        t.Db.CampaignMemberships.Add(mem);
        t.Db.SaveChanges();

        Seed(t, DateTime.UtcNow.AddDays(-120), FaceImageKind.Reference, refKey, camp.Id, candidateId);

        // Ảnh của campaign đã soft-delete VẪN phải bị dọn (không FK/query-filter nào được che nó).
        Assert.Equal(1, await PurgeOnce(Build(t, files)));

        using var db = t.NewContext();
        var sau = await db.CampaignMemberships.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(m => m.Id == mem.Id);
        Assert.Null(sau.ReferenceImageKey);
        Assert.Contains(refKey, files.Deleted);
    }

    [Fact]
    public async Task Purge_BangRong_KhongLoi()
    {
        using var t = new CampaignTestDb();
        Assert.Equal(0, await PurgeOnce(Build(t, new FakeFileService())));
    }
}
