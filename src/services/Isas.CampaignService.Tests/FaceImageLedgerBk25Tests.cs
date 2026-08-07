using System.Security.Claims;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BK25/DATA-3 — mọi ảnh sinh trắc đẩy lên S3 phải để lại DẤU VẾT trong <c>face_images</c>.
/// Trước task này <c>face-check</c> upload 1 ảnh mỗi ~30 giây rồi vứt key ⇒ object mồ côi, không
/// liệt kê nổi, không join nổi để dọn.
///
/// Bất biến được khoá ở đây: <b>không object nào tồn tại trong S3 mà không có dòng trỏ tới</b>
///  • ghi sổ TRƯỚC upload (test <c>GhiSo_TruocKhi_Upload</c> — chen vào giữa để chứng minh thứ tự);
///  • DATA-2 "ảnh tham chiếu 1 bản/ứng viên/campaign": enroll lại đổi ĐUÔI FILE sinh key khác ⇒
///    bản cũ phải bị dọn, không được để lại;
///  • dọn bản cũ hỏng (S3 lỗi) → GIỮ dòng sổ để purger nhặt sau, không được nuốt dấu vết.
/// </summary>
public class FaceImageLedgerBk25Tests
{
    private static readonly Guid FixedSession = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ── test double cho S3: ghi lại key đã upload/xoá + hook chạy GIỮA lúc upload ───
    private sealed class FakeFileService : IFileService
    {
        public readonly List<string> Uploaded = new();
        public readonly List<string> Deleted = new();
        public Func<string, Task>? OnUpload;      // chạy TRONG UploadAsync → soi trạng thái DB lúc đó
        public string? FailDeleteOnKey;

        public async Task<string> UploadAsync(IFormFile file, string path, CancellationToken ct = default)
        {
            if (OnUpload is not null) await OnUpload(path);
            Uploaded.Add(path);
            return path;
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            if (FailDeleteOnKey is not null && path == FailDeleteOnKey)
                throw new InvalidOperationException($"S3 down for {path}");
            Deleted.Add(path);
            return Task.CompletedTask;
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public string GetUrl(string path) => path;
    }

    private static FaceVerifyController NewController(
        CampaignDbContext db, Guid candidateId, IFileService file, IAiServiceFaceVerifyClient ai)
    {
        var controller = new FaceVerifyController(db, file, ai, Mock.Of<ILogger<FaceVerifyController>>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, candidateId.ToString()) }, "Test"))
            }
        };
        return controller;
    }

    private static IFormFile FakeImage(string fileName = "face.jpg")
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    private static IAiServiceFaceVerifyClient AiOk()
    {
        var m = new Mock<IAiServiceFaceVerifyClient>();
        m.Setup(x => x.VerifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceVerifyResult(1, true, 0.9f, new List<string>()));
        return m.Object;
    }

    private static (Campaign campaign, Guid candidateId) SeedMember(
        CampaignTestDb t, bool faceVerify = true, string? referenceImageKey = null)
    {
        var c = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active, antiCheat: false);
        c.FaceVerifyEnabled = faceVerify;
        t.Db.Campaigns.Add(c);

        var candidateId = Guid.NewGuid();
        var mem = CampaignTestDb.NewMembership(
            c.Id, candidateId, sessionId: FixedSession, referenceImageKey: referenceImageKey);
        t.Db.CampaignMemberships.Add(mem);
        t.Db.SaveChanges();
        return (c, candidateId);
    }

    // ── (1) face-check: mỗi ảnh giám sát để lại đúng 1 dòng sổ ─────────────────────
    // MUTATION-CHECK — gỡ RecordImageAsync khỏi Check() thì test này ĐỎ: quay lại đúng bug BK25
    // (ảnh 30s/lần lên S3 mà không có gì trỏ tới).
    [Fact]
    public async Task Check_GhiDongSo_Live_KemSession_VaDungKey()
    {
        using var t = new CampaignTestDb();
        var (camp, candidateId) = SeedMember(t, referenceImageKey: "ref/key.jpg");
        var files = new FakeFileService();

        await NewController(t.Db, candidateId, files, AiOk())
            .Check(camp.Id, FixedSession, FakeImage(), CancellationToken.None);

        using var db = t.NewContext();
        var row = Assert.Single(await db.FaceImages.AsNoTracking().ToListAsync());
        Assert.Equal(FaceImageKind.Live, row.Kind);
        Assert.Equal(FixedSession, row.SessionId);
        Assert.Equal(camp.Id, row.CampaignId);
        Assert.Equal(candidateId, row.CandidateId);
        // Sổ phải trỏ ĐÚNG object vừa đẩy lên — lệch key thì purge sẽ xoá nhầm/không xoá được.
        Assert.Equal(Assert.Single(files.Uploaded), row.StorageKey);
    }

    // ── (2) nhiều lần check → đếm được (chính là thứ trước đây không làm nổi) ───────
    [Fact]
    public async Task Check_NhieuLan_MoiAnh_MotDong_DemDuoc()
    {
        using var t = new CampaignTestDb();
        var (camp, candidateId) = SeedMember(t, referenceImageKey: "ref/key.jpg");
        var files = new FakeFileService();

        for (var i = 0; i < 3; i++)
            await NewController(t.Db, candidateId, files, AiOk())
                .Check(camp.Id, FixedSession, FakeImage(), CancellationToken.None);

        using var db = t.NewContext();
        Assert.Equal(3, await db.FaceImages.CountAsync(x => x.Kind == FaceImageKind.Live));
        Assert.Equal(3, files.Uploaded.Count);
        Assert.Equal(3, files.Uploaded.Distinct().Count());   // key live phải khác nhau từng lần
    }

    // ── (3) THỨ TỰ: dòng sổ phải durable TRƯỚC khi object lên S3 ───────────────────
    // MUTATION-CHECK — đảo thứ tự (upload trước, ghi sổ sau) thì test này ĐỎ. Thứ tự đó tạo cửa sổ
    // mà process chết giữa chừng để lại ảnh khuôn mặt không ai trỏ tới; chiều hiện tại chết giữa
    // chừng chỉ để lại dòng sổ trỏ vào object chưa tồn tại (purge DeleteObject là no-op → tự lành).
    [Fact]
    public async Task Check_GhiSo_TruocKhi_Upload()
    {
        using var t = new CampaignTestDb();
        var (camp, candidateId) = SeedMember(t, referenceImageKey: "ref/key.jpg");

        var thayDongSoLucUpload = false;
        var files = new FakeFileService();
        files.OnUpload = async key =>
        {
            // Context RIÊNG → chỉ đọc được thứ đã COMMIT, không phải thứ còn trong change tracker.
            using var probe = t.NewContext();
            thayDongSoLucUpload = await probe.FaceImages.AnyAsync(x => x.StorageKey == key);
        };

        await NewController(t.Db, candidateId, files, AiOk())
            .Check(camp.Id, FixedSession, FakeImage(), CancellationToken.None);

        Assert.True(thayDongSoLucUpload,
            "Dòng face_images phải được commit TRƯỚC khi object lên S3 (bất biến BK25).");
    }

    // ── (4) campaign tắt face-verify → không upload thì cũng không ghi sổ ──────────
    [Fact]
    public async Task Check_FaceVerifyTat_KhongUpload_KhongGhiSo()
    {
        using var t = new CampaignTestDb();
        var (camp, candidateId) = SeedMember(t, faceVerify: false);
        var files = new FakeFileService();

        await NewController(t.Db, candidateId, files, AiOk())
            .Check(camp.Id, FixedSession, FakeImage(), CancellationToken.None);

        Assert.Empty(files.Uploaded);
        Assert.Equal(0, await t.NewContext().FaceImages.CountAsync());
    }

    // ── (5) enroll → dòng sổ Reference (không gắn buổi thi nào) ───────────────────
    [Fact]
    public async Task Enroll_GhiDongSo_Reference_KhongSession()
    {
        using var t = new CampaignTestDb();
        var (camp, candidateId) = SeedMember(t);
        var files = new FakeFileService();

        await NewController(t.Db, candidateId, files, AiOk())
            .Enroll(camp.Id, FixedSession, FakeImage(), CancellationToken.None);

        using var db = t.NewContext();
        var row = Assert.Single(await db.FaceImages.AsNoTracking().ToListAsync());
        Assert.Equal(FaceImageKind.Reference, row.Kind);
        Assert.Null(row.SessionId);
        Assert.Equal(Assert.Single(files.Uploaded), row.StorageKey);
    }

    // ── (6) DATA-2: enroll lại CÙNG đuôi file → vẫn đúng 1 object, 1 dòng ─────────
    // Key deterministic nên object bị ghi đè; sổ chỉ dời CapturedAt (không đẻ dòng thứ hai, không vỡ
    // UNIQUE(storage_key)). Hạn giữ tính lại từ lần enroll mới — đúng: object trong S3 là ảnh MỚI.
    [Fact]
    public async Task Enroll_Lai_CungDuoiFile_VanDungMotDong_VaDoiCapturedAt()
    {
        using var t = new CampaignTestDb();
        var (camp, candidateId) = SeedMember(t);
        var files = new FakeFileService();

        await NewController(t.Db, candidateId, files, AiOk())
            .Enroll(camp.Id, FixedSession, FakeImage("a.jpg"), CancellationToken.None);
        var lanDau = (await t.NewContext().FaceImages.AsNoTracking().SingleAsync()).CapturedAt;

        await Task.Delay(10);
        await NewController(t.Db, candidateId, files, AiOk())
            .Enroll(camp.Id, FixedSession, FakeImage("b.jpg"), CancellationToken.None);

        using var db = t.NewContext();
        var row = Assert.Single(await db.FaceImages.AsNoTracking().ToListAsync());
        Assert.True(row.CapturedAt > lanDau);
        Assert.Empty(files.Deleted);          // cùng key → không có bản cũ nào bị bỏ lại để dọn
    }

    // ── (7) DATA-2: enroll lại ĐỔI đuôi file → key khác → bản cũ phải bị dọn ──────
    // MUTATION-CHECK — gỡ DeleteSupersededReferenceAsync thì test này ĐỎ. Đây chính là lỗ DATA-2
    // ("1 bản/ứng viên/campaign") mà brief yêu cầu kiểm: BuildKey nối `Path.GetExtension`, nên
    // .jpg rồi .png để lại HAI object trong khi membership chỉ trỏ được vào cái sau.
    [Fact]
    public async Task Enroll_Lai_DoiDuoiFile_DonBanCu_ConDungMotBan()
    {
        using var t = new CampaignTestDb();
        var (camp, candidateId) = SeedMember(t);
        var files = new FakeFileService();

        await NewController(t.Db, candidateId, files, AiOk())
            .Enroll(camp.Id, FixedSession, FakeImage("a.jpg"), CancellationToken.None);
        await NewController(t.Db, candidateId, files, AiOk())
            .Enroll(camp.Id, FixedSession, FakeImage("a.png"), CancellationToken.None);

        var keyCu = files.Uploaded[0];
        var keyMoi = files.Uploaded[1];
        Assert.NotEqual(keyCu, keyMoi);

        Assert.Equal(new[] { keyCu }, files.Deleted);              // bản bị thay thế rời S3
        using var db = t.NewContext();
        var row = Assert.Single(await db.FaceImages.AsNoTracking().ToListAsync());
        Assert.Equal(keyMoi, row.StorageKey);                      // sổ còn đúng 1 dòng = 1 object
        var mem = await db.CampaignMemberships.AsNoTracking().SingleAsync();
        Assert.Equal(keyMoi, mem.ReferenceImageKey);
    }

    // ── (8) dọn bản cũ hỏng → GIỮ dòng sổ, và enroll VẪN thành công ───────────────
    // MUTATION-CHECK — bỏ `return` trong catch (tức xoá dòng sổ dù S3 lỗi) thì test này ĐỎ: mất dấu
    // vết của một object VẪN CÒN trong S3. Và bỏ try/catch thì enroll trả 500 dù đã commit xong.
    [Fact]
    public async Task Enroll_DonBanCu_S3Loi_GiuDongSo_VaKhongLamHongEnroll()
    {
        using var t = new CampaignTestDb();
        var (camp, candidateId) = SeedMember(t);
        var files = new FakeFileService();

        await NewController(t.Db, candidateId, files, AiOk())
            .Enroll(camp.Id, FixedSession, FakeImage("a.jpg"), CancellationToken.None);
        files.FailDeleteOnKey = files.Uploaded[0];

        var ex = await Record.ExceptionAsync(() => NewController(t.Db, candidateId, files, AiOk())
            .Enroll(camp.Id, FixedSession, FakeImage("a.png"), CancellationToken.None));

        Assert.Null(ex);                                  // enroll mới KHÔNG được hỏng vì việc dọn rác
        using var db = t.NewContext();
        var keys = await db.FaceImages.AsNoTracking().Select(x => x.StorageKey).ToListAsync();
        Assert.Equal(2, keys.Count);                      // dấu vết bản cũ CÒN → purger dọn khi tới hạn
        Assert.Contains(files.Uploaded[0], keys);
        var mem = await db.CampaignMemberships.AsNoTracking().SingleAsync();
        Assert.Equal(files.Uploaded[1], mem.ReferenceImageKey);
    }
}
