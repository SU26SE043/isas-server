using System.Net;
using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Isas.InterviewService.Tests;
using Xunit;

/// <summary>
/// Soft-delete file CV/JD (2026-09-15). Ba bất biến khoá ở đây, mỗi cái ứng với một lỗi đã đo trên prod:
/// (1) xoá = đóng dấu DB TRƯỚC, S3 SAU, và S3 lỗi KHÔNG làm mất dấu (đường cũ xoá S3 trước rồi DB bị
///     FK RESTRICT từ chối ⇒ 9/49 row "zombie");
/// (2) file đã xoá biến mất khỏi MỌI đường đọc của người dùng (list · metadata · parsed-text · dùng cho
///     buổi mới) nhưng row + tham chiếu của buổi/roadmap giữ nguyên;
/// (3) tải file có row mà mất object ⇒ 404 (lỗi vĩnh viễn), không phải 500 "thử lại".
/// </summary>
public class FileSoftDeleteTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    private static (StorageService svc, Mock<IAmazonS3> s3, TestDb tdb) Build()
    {
        var tdb = new TestDb();
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());
        var opts = Options.Create(new FileStorageOptions { ServiceURL = "http://s3", BucketName = "isas-files" });
        var svc = new StorageService(NullLogger<StorageService>.Instance, s3.Object, opts, tdb.Db);
        return (svc, s3, tdb);
    }

    private static FileRecord Seed(TestDb tdb, Guid? owner = null, DateTime? deletedAt = null)
    {
        var f = new FileRecord
        {
            Id = Guid.NewGuid(), UserId = owner ?? Owner, FileType = "cv", OriginalName = "cv.pdf",
            StoragePath = $"cv/{Guid.NewGuid()}.pdf", StorageBucket = "isas-files", MimeType = "application/pdf",
            FileSize = 10, ParsedText = "NOI DUNG CV", ParseStatus = "completed",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, DeletedAt = deletedAt,
        };
        tdb.Db.FileRecords.Add(f); tdb.Db.SaveChanges();
        return f;
    }

    // ── (1) thứ tự xoá ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Xoa_DongDauDbTruoc_RoiMoiXoaS3_RowVanCon()
    {
        var (svc, s3, tdb) = Build();
        var f = Seed(tdb);
        var order = new List<string>();
        s3.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                // Lúc S3 được gọi, DB PHẢI đã đóng dấu (đọc bằng context riêng để không tin change tracker).
                using var fresh = tdb.NewContext();
                var row = fresh.FileRecords.Single(x => x.Id == f.Id);
                order.Add(row.DeletedAt is null ? "s3-before-db" : "db-then-s3");
            })
            .ReturnsAsync(new DeleteObjectResponse());

        Assert.True(await svc.DeleteFileRecord(f.Id));

        Assert.Equal(new[] { "db-then-s3" }, order);
        using var check = tdb.NewContext();
        var kept = check.FileRecords.Single(x => x.Id == f.Id);   // row KHÔNG bị Remove — FK của buổi luyện an toàn
        Assert.NotNull(kept.DeletedAt);
    }

    [Fact]
    public async Task S3Loi_VanDongDauDb_KhongNem_KhongZombie()
    {
        var (svc, s3, tdb) = Build();
        var f = Seed(tdb);
        s3.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("boom"));

        Assert.True(await svc.DeleteFileRecord(f.Id));

        using var check = tdb.NewContext();
        Assert.NotNull(check.FileRecords.Single(x => x.Id == f.Id).DeletedAt);
    }

    [Fact]
    public async Task XoaLanHai_Idempotent_KhongGoiS3Lai()
    {
        var (svc, s3, tdb) = Build();
        var f = Seed(tdb);
        Assert.True(await svc.DeleteFileRecord(f.Id));
        Assert.True(await svc.DeleteFileRecord(f.Id));
        s3.Verify(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(await svc.DeleteFileRecord(Guid.NewGuid()));   // không tồn tại → false
    }

    // ── (2) đã xoá thì mọi đường đọc của người dùng KHÔNG thấy ───────────────────

    [Fact]
    public async Task DaXoa_KhongCoTrongList_Metadata_ParsedText_VaBuoiMoi()
    {
        var (svc, _, tdb) = Build();
        var alive = Seed(tdb);
        var dead = Seed(tdb, deletedAt: DateTime.UtcNow);

        var page = await svc.GetFilesByUserId(Owner);
        Assert.Contains(page.Items, i => i.Id == alive.Id);
        Assert.DoesNotContain(page.Items, i => i.Id == dead.Id);

        Assert.NotNull(await svc.GetMetadata(alive.Id));
        Assert.Null(await svc.GetMetadata(dead.Id));                        // như không tồn tại
        Assert.NotNull(await svc.GetMetadata(dead.Id, includeDeleted: true)); // chỉ đường xoá dùng

        Assert.Equal("NOI DUNG CV", await svc.GetOwnedParsedTextAsync(alive.Id, Owner));
        Assert.Equal(string.Empty, await svc.GetOwnedParsedTextAsync(dead.Id, Owner));   // không dùng được cho buổi MỚI
        Assert.Equal(string.Empty, await svc.GetParseTextAsync(dead.Id));
    }

    // ── (3) tải file có row mà mất object ⇒ 404 ─────────────────────────────────

    private static InterviewController Controller(Mock<IStorageService> storage, Guid caller)
        => new(Mock.Of<ICVParserService>(), storage.Object, NullLogger<InterviewController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, caller.ToString()) }, "test")),
                },
            },
        };

    [Fact]
    public async Task Download_ObjectMat_404_KhongPhai500()
    {
        var storage = new Mock<IStorageService>();
        var f = new FileRecord { Id = Guid.NewGuid(), UserId = Owner, OriginalName = "cv.pdf", MimeType = "application/pdf", StoragePath = "cv/x.pdf", ParseStatus = "completed" };
        storage.Setup(s => s.GetMetadata(f.Id, It.IsAny<CancellationToken>())).ReturnsAsync(f);
        storage.Setup(s => s.DownloadAsync(f.StoragePath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("The specified key does not exist.") { ErrorCode = "NoSuchKey", StatusCode = HttpStatusCode.NotFound });

        var result = await Controller(storage, Owner).DownloadFile(f.Id, CancellationToken.None);

        var nf = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("không còn", nf.Value!.ToString()!);
    }

    [Fact]
    public async Task Delete_FileDaXoa_VanTra204()
    {
        var storage = new Mock<IStorageService>();
        var f = new FileRecord { Id = Guid.NewGuid(), UserId = Owner, OriginalName = "cv.pdf", MimeType = "application/pdf", StoragePath = "cv/x.pdf", ParseStatus = "completed", DeletedAt = DateTime.UtcNow };
        storage.Setup(s => s.GetMetadata(f.Id, true, It.IsAny<CancellationToken>())).ReturnsAsync(f);
        storage.Setup(s => s.DeleteFileRecord(f.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Controller(storage, Owner).DeleteFile(f.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }
}
