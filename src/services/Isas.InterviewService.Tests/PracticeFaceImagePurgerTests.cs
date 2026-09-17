using System.Reflection;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Tests;

/// <summary>
/// B2C coaching (2026-09-17) — retention ảnh webcam. Mirror
/// <c>Isas.CampaignService.Tests.FaceImagePurgerBk25Tests</c> (BK25), bớt vế "gỡ con trỏ ảnh THAM
/// CHIẾU" — B2C không có khái niệm enroll/reference.
/// </summary>
public class PracticeFaceImagePurgerTests
{
    private sealed class FakeStorageService : IStorageService
    {
        public readonly List<string> Deleted = new();
        public string? FailOnKey;

        public Task DeleteObjectAsync(string key, CancellationToken ct = default)
        {
            if (FailOnKey is not null && key == FailOnKey)
                throw new InvalidOperationException($"S3 down for {key}");
            Deleted.Add(key);
            return Task.CompletedTask;
        }

        // Các thành viên khác của IStorageService không dùng tới ở job này — ném để lộ ngay nếu
        // purger vô tình chạm phải đường không liên quan.
        public Task UploadObjectAsync(string key, Stream stream, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> UploadAsync(Stream fileStream, string fileType, Guid userId, Guid fileId, string ext, string contentType = "application/octet-stream", CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct = default)
            => throw new NotSupportedException();
        public string GetPresignedUrl(string storagePath, int expiryMinutes = 60)
            => throw new NotSupportedException();
        public Task<Isas.InterviewService.Entities.FileRecord> SaveMetadata(Guid fileId, Guid userId, string fileType, string originalName, string storagePath, string storageBucket, string mimeType, long fileSize, Isas.InterviewService.DTOs.CVParseResult? parsedCv, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Isas.InterviewService.Entities.FileRecord?> GetMetadata(Guid fileId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Isas.InterviewService.Entities.FileRecord?> GetMetadata(Guid fileId, bool includeDeleted, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> GetParseTextAsync(Guid fileId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> GetOwnedParsedTextAsync(Guid fileId, Guid ownerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Isas.Shared.Pagination.KeysetPage<Isas.InterviewService.DTOs.FileRecordSummary>> GetFilesByUserId(Guid userId, string? fileType = null, string? cursor = null, int? limit = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Isas.InterviewService.Entities.FileRecord> UpdateFileRecord(Guid fileId, Stream stream, string originalName, long fileSize, string contentType, Isas.InterviewService.DTOs.CVParseResult? parsedCv, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteFileRecord(Guid fileId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static async Task<int> PurgeOnce(PracticeFaceImagePurger p)
    {
        var mi = typeof(PracticeFaceImagePurger)
            .GetMethod("PurgeOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<int>)mi.Invoke(p, new object[] { CancellationToken.None })!;
    }

    private static PracticeFaceImagePurger Build(
        TestDb t, FakeStorageService storage, FaceImageRetentionSettings? settings = null)
    {
        var provider = new ServiceCollection()
            .AddDbContext<InterviewDbContext>(o => o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention())
            .AddSingleton<IStorageService>(storage)
            .BuildServiceProvider();

        return new PracticeFaceImagePurger(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings ?? new FaceImageRetentionSettings
            {
                Enabled = true,
                RetentionDays = 90,
                BatchSize = 200
            }),
            NullLogger<PracticeFaceImagePurger>.Instance);
    }

    private static PracticeFaceImage Seed(TestDb t, DateTime capturedAt, string? key = null)
    {
        var row = new PracticeFaceImage
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            CandidateId = Guid.NewGuid(),
            StorageKey = key ?? $"practice-face/x/y/{Guid.NewGuid():N}.jpg",
            CapturedAt = capturedAt
        };
        t.Db.PracticeFaceImages.Add(row);
        t.Db.SaveChanges();
        return row;
    }

    // MUTATION-CHECK — nới vị ngữ retention (bỏ `x.CapturedAt < cutoff`) thì test này ĐỎ.
    [Fact]
    public async Task Purge_KhongXoa_AnhConTrongHan()
    {
        using var t = new TestDb();
        var storage = new FakeStorageService();
        var moiChup = Seed(t, DateTime.UtcNow.AddDays(-2));

        var deleted = await PurgeOnce(Build(t, storage));

        Assert.Equal(0, deleted);
        Assert.Empty(storage.Deleted);
        Assert.True(await t.NewContext().PracticeFaceImages.AnyAsync(x => x.Id == moiChup.Id));
    }

    // Đường chính — quá hạn thì xoá CẢ HAI VẾ (S3 lẫn dòng sổ).
    [Fact]
    public async Task Purge_XoaCa_ObjectS3_LanDongSo_KhiQuaHan()
    {
        using var t = new TestDb();
        var storage = new FakeStorageService();
        var rac = Seed(t, DateTime.UtcNow.AddDays(-120), key: "practice-face/a/b/old.jpg");
        var giu = Seed(t, DateTime.UtcNow.AddDays(-1));

        var deleted = await PurgeOnce(Build(t, storage));

        Assert.Equal(1, deleted);
        Assert.Equal(new[] { "practice-face/a/b/old.jpg" }, storage.Deleted);
        using var db = t.NewContext();
        Assert.False(await db.PracticeFaceImages.AnyAsync(x => x.Id == rac.Id));
        Assert.True(await db.PracticeFaceImages.AnyAsync(x => x.Id == giu.Id));
    }

    // MUTATION-CHECK — gỡ `if (!_options.Enabled) return 0;` thì test này ĐỎ.
    [Fact]
    public async Task Purge_TatBangConfig_KhongXoaGi()
    {
        using var t = new TestDb();
        var storage = new FakeStorageService();
        Seed(t, DateTime.UtcNow.AddDays(-365));

        var deleted = await PurgeOnce(Build(t, storage, new FaceImageRetentionSettings
        {
            Enabled = false, RetentionDays = 90, BatchSize = 200
        }));

        Assert.Equal(0, deleted);
        Assert.Empty(storage.Deleted);
        Assert.Equal(1, await t.NewContext().PracticeFaceImages.CountAsync());
    }

    // Mặc định phải là TẮT — xoá dữ liệu bằng chứng không đảo ngược được.
    [Fact]
    public async Task Purge_MacDinh_La_TAT()
    {
        var macDinh = new FaceImageRetentionSettings();
        Assert.False(macDinh.Enabled);
        Assert.Equal(90, macDinh.RetentionDays);

        using var t = new TestDb();
        var storage = new FakeStorageService();
        Seed(t, DateTime.UtcNow.AddDays(-365));

        Assert.Equal(0, await PurgeOnce(Build(t, storage, macDinh)));
        Assert.Equal(1, await t.NewContext().PracticeFaceImages.CountAsync());
    }

    // MUTATION-CHECK — gỡ `.Take(batch)` thì test này ĐỎ.
    [Fact]
    public async Task Purge_TonTrong_TranBatch_MoiVong()
    {
        using var t = new TestDb();
        var storage = new FakeStorageService();
        for (var i = 0; i < 5; i++)
            Seed(t, DateTime.UtcNow.AddDays(-120 + i));

        var purger = Build(t, storage, new FaceImageRetentionSettings
        {
            Enabled = true, RetentionDays = 90, BatchSize = 2
        });

        Assert.Equal(2, await PurgeOnce(purger));
        Assert.Equal(3, await t.NewContext().PracticeFaceImages.CountAsync());
        Assert.Equal(2, storage.Deleted.Count);

        Assert.Equal(2, await PurgeOnce(purger));
        Assert.Equal(1, await PurgeOnce(purger));
        Assert.Equal(0, await t.NewContext().PracticeFaceImages.CountAsync());
        Assert.Equal(5, storage.Deleted.Count);
    }

    // MUTATION-CHECK — ĐẢO THỨ TỰ (xoá dòng DB trước, S3 sau) hoặc bỏ `continue` khi S3 lỗi thì test
    // này ĐỎ: dòng sổ biến mất trong khi ảnh vẫn nằm trong S3 = mồ côi = con bug BK25 sinh ra để diệt.
    [Fact]
    public async Task Purge_S3Loi_GiuDongSo_DeVongSauThuLai()
    {
        using var t = new TestDb();
        var storage = new FakeStorageService { FailOnKey = "practice-face/a/b/hong.jpg" };
        var hong = Seed(t, DateTime.UtcNow.AddDays(-120), key: "practice-face/a/b/hong.jpg");
        var ok = Seed(t, DateTime.UtcNow.AddDays(-120), key: "practice-face/a/b/ok.jpg");

        var deleted = await PurgeOnce(Build(t, storage));

        Assert.Equal(1, deleted);
        using var db = t.NewContext();
        Assert.True(await db.PracticeFaceImages.AnyAsync(x => x.Id == hong.Id));
        Assert.False(await db.PracticeFaceImages.AnyAsync(x => x.Id == ok.Id));

        storage.FailOnKey = null;
        Assert.Equal(1, await PurgeOnce(Build(t, storage)));
        Assert.Equal(0, await t.NewContext().PracticeFaceImages.CountAsync());
    }

    [Fact]
    public async Task Purge_BangRong_KhongLoi()
    {
        using var t = new TestDb();
        Assert.Equal(0, await PurgeOnce(Build(t, new FakeStorageService())));
    }
}
