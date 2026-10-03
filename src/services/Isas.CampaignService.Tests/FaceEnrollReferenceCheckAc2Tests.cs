using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// AC2 · B1 — face-enroll chỉ nhận ảnh mốc có ĐÚNG 1 khuôn mặt.
/// Prod 03/10 (buổi ca67b394…): ảnh mốc có 2 mặt (mặt phụ 92×112 ở mép, tin cậy 0.81) được nhận không
/// kiểm ⇒ cả buổi face-check chỉ ra identity_unverified hoặc multiple_faces, danh tính không lần nào xác minh.
///
/// Bất biến khoá ở đây:
///  • mốc ĐANG DÙNG không bao giờ bị ghi đè/xoá TRƯỚC khi ảnh mới được chấp nhận (ảnh mới vào key riêng);
///  • ảnh bị loại: membership không đổi, mốc cũ còn nguyên, object mới + dòng sổ mới bị dọn (S3 trước);
///  • AIService lỗi ⇒ FAIL-OPEN (SEC-5: buổi đã giữ suất + đồng hồ đang chạy, không chặn vì hệ thống hỏng);
///  • BK25 vẫn giữ: ghi sổ TRƯỚC upload.
/// </summary>
public class FaceEnrollReferenceCheckAc2Tests
{
    private static readonly Guid FixedSession = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // ── S3 giả: ghi lại upload/xoá + hook chạy TRONG lúc upload/xoá để soi trạng thái DB lúc đó ─────
    private sealed class FakeFileService : IFileService
    {
        public readonly List<string> Uploaded = new();
        public readonly List<string> Deleted = new();
        public Func<string, Task>? OnUpload;
        public Func<string, Task>? OnDelete;
        public string? FailDeleteOnKey;

        public async Task<string> UploadAsync(IFormFile file, string path, CancellationToken ct = default)
        {
            if (OnUpload is not null) await OnUpload(path);
            Uploaded.Add(path);
            return path;
        }

        public async Task DeleteAsync(string path, CancellationToken ct = default)
        {
            if (OnDelete is not null) await OnDelete(path);
            if (FailDeleteOnKey is not null && path == FailDeleteOnKey)
                throw new InvalidOperationException($"S3 down for {path}");
            Deleted.Add(path);
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public string GetUrl(string path) => path;
    }

    // Logger ghi lại để khoá yêu cầu "LogWarning nêu rõ chưa kiểm được số khuôn mặt".
    private sealed class CapturingLogger : ILogger<FaceVerifyController>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static FaceVerifyController NewController(
        CampaignDbContext db, Guid candidateId, IFileService file, IAiServiceFaceVerifyClient ai,
        ILogger<FaceVerifyController>? logger = null)
    {
        var controller = new FaceVerifyController(db, file, ai, logger ?? NullLogger<FaceVerifyController>.Instance);
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

    private static Mock<IAiServiceFaceVerifyClient> AiDetecting(int faceCount, Func<string, Task>? onDetect = null)
    {
        var m = new Mock<IAiServiceFaceVerifyClient>();
        m.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string key, CancellationToken _) =>
            {
                if (onDetect is not null) await onDetect(key);
                var signals = faceCount == 0 ? new List<string> { "no_face" }
                    : faceCount > 1 ? new List<string> { "multiple_faces" } : new List<string>();
                return new FaceDetectResult(faceCount, signals);
            });
        return m;
    }

    private static string LegacyKey(Guid campaignId, Guid candidateId)
        => $"campaigns/{campaignId}/candidates/{candidateId}/face-reference.jpg";

    /// <summary>Campaign Active bật face-verify + membership đang giữ buổi FixedSession; tuỳ chọn có sẵn
    /// mốc cũ (kèm dòng sổ như production — BK25 backfill).</summary>
    private static (Campaign campaign, Guid candidateId) Seed(CampaignTestDb t, Func<Guid, Guid, string>? existingKey = null)
    {
        var c = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        c.FaceVerifyEnabled = true;
        t.Db.Campaigns.Add(c);
        var candidateId = Guid.NewGuid();
        var refKey = existingKey?.Invoke(c.Id, candidateId);
        t.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(
            c.Id, candidateId, sessionId: FixedSession, referenceImageKey: refKey));
        if (refKey is not null)
            t.Db.FaceImages.Add(new FaceImage
            {
                Id = Guid.NewGuid(), CampaignId = c.Id, CandidateId = candidateId, SessionId = null,
                Kind = FaceImageKind.Reference, StorageKey = refKey, CapturedAt = DateTime.UtcNow.AddMinutes(-5)
            });
        t.Db.SaveChanges();
        return (c, candidateId);
    }

    private static string? ReferenceKeyInDb(CampaignTestDb t)
        => t.NewContext().CampaignMemberships.AsNoTracking().Single().ReferenceImageKey;

    private static List<string> LedgerKeys(CampaignTestDb t)
        => t.NewContext().FaceImages.AsNoTracking().Select(x => x.StorageKey).OrderBy(x => x).ToList();

    private static JsonElement Body(IActionResult r)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(r);
        return JsonSerializer.SerializeToElement(bad.Value);
    }

    // ── (1) đúng 1 mặt → 204, mốc = key MỚI (key riêng), mốc cũ bị dọn (S3 + sổ) ───────────────────
    [Fact]
    public async Task Enroll_MotMat_204_KeyMoi_DonMocCu()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, LegacyKey);
        var oldKey = LegacyKey(camp.Id, cand);
        var files = new FakeFileService();

        var result = await NewController(t.NewContext(), cand, files, AiDetecting(1).Object)
            .Enroll(camp.Id, FixedSession, FakeImage(), default);

        Assert.IsType<NoContentResult>(result);
        var newKey = Assert.Single(files.Uploaded);
        Assert.Matches(new Regex($"^campaigns/{camp.Id}/candidates/{cand}/face-reference-[0-9a-f]{{32}}\\.jpg$"), newKey);
        Assert.Equal(newKey, ReferenceKeyInDb(t));
        Assert.Equal(new[] { oldKey }, files.Deleted);                 // mốc cũ rời S3
        Assert.Equal(new[] { newKey }, LedgerKeys(t));                 // sổ còn đúng 1 dòng = 1 object
    }

    // ── (2) 2 mặt → 400 REFERENCE_MULTIPLE_FACES; mốc cũ + object cũ còn nguyên; object mới + dòng sổ mới bị dọn ─
    [Fact]
    public async Task Enroll_HaiMat_400_MultipleFaces_GiuNguyenMocCu_DonAnhMoi()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, LegacyKey);
        var oldKey = LegacyKey(camp.Id, cand);
        var files = new FakeFileService();

        var result = await NewController(t.NewContext(), cand, files, AiDetecting(2).Object)
            .Enroll(camp.Id, FixedSession, FakeImage(), default);

        var body = Body(result);
        Assert.Equal("REFERENCE_MULTIPLE_FACES", body.GetProperty("code").GetString());
        Assert.Equal(2, body.GetProperty("faceCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));

        var newKey = Assert.Single(files.Uploaded);
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(oldKey, ReferenceKeyInDb(t));                     // membership KHÔNG đổi
        Assert.Equal(new[] { newKey }, files.Deleted);                 // chỉ ảnh mới bị xoá, mốc cũ KHÔNG
        Assert.Equal(new[] { oldKey }, LedgerKeys(t));                 // dòng sổ mới bị dọn, dòng cũ còn
    }

    // ── (3) 0 mặt → 400 REFERENCE_NO_FACE; chưa có mốc thì vẫn chưa có mốc ─────────────────────────
    [Fact]
    public async Task Enroll_KhongMat_400_NoFace_MembershipVanChuaCoMoc()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t);
        var files = new FakeFileService();

        var result = await NewController(t.NewContext(), cand, files, AiDetecting(0).Object)
            .Enroll(camp.Id, FixedSession, FakeImage(), default);

        var body = Body(result);
        Assert.Equal("REFERENCE_NO_FACE", body.GetProperty("code").GetString());
        Assert.Equal(0, body.GetProperty("faceCount").GetInt32());
        Assert.Null(ReferenceKeyInDb(t));
        Assert.Equal(files.Uploaded, files.Deleted);
        Assert.Empty(LedgerKeys(t));
    }

    // ── (4) AIService lỗi → FAIL-OPEN: 204 + mốc mới được dùng + LogWarning nêu rõ ───────────────────
    [Fact]
    public async Task Enroll_AiServiceLoi_FailOpen_204_DungMocMoi_VaCanhBao()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, LegacyKey);
        var oldKey = LegacyKey(camp.Id, cand);
        var files = new FakeFileService();
        var ai = new Mock<IAiServiceFaceVerifyClient>();
        ai.Setup(x => x.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamServiceException("AIService face-detect hết giờ."));
        var log = new CapturingLogger();

        var result = await NewController(t.NewContext(), cand, files, ai.Object, log)
            .Enroll(camp.Id, FixedSession, FakeImage(), default);

        Assert.IsType<NoContentResult>(result);
        var newKey = Assert.Single(files.Uploaded);
        Assert.Equal(newKey, ReferenceKeyInDb(t));
        Assert.Equal(new[] { oldKey }, files.Deleted);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("CHƯA KIỂM ĐƯỢC SỐ KHUÔN MẶT", StringComparison.Ordinal));
    }

    // ── (5) KHÔNG BAO GIỜ ghi đè key mốc đang dùng TRƯỚC khi kiểm ─────────────────────────────────────
    // Mốc đang dùng là key deterministic thời trước AC2 — đúng key mà code cũ sẽ upload đè lên. Soi
    // tại thời điểm AIService được hỏi: thứ đang được kiểm phải là key KHÁC, membership vẫn trỏ mốc cũ,
    // và S3 chưa hề nhận ghi vào key mốc cũ.
    [Fact]
    public async Task Enroll_KhongBaoGioGhiDeKeyMocDangDung_TruocKhiKiem()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t, LegacyKey);
        var oldKey = LegacyKey(camp.Id, cand);
        var files = new FakeFileService();
        string? checkedKey = null;
        string? refAtCheck = null;
        List<string>? uploadedAtCheck = null;
        var ai = AiDetecting(2, key =>
        {
            checkedKey = key;
            refAtCheck = ReferenceKeyInDb(t);
            uploadedAtCheck = files.Uploaded.ToList();
            return Task.CompletedTask;
        });

        await NewController(t.NewContext(), cand, files, ai.Object)
            .Enroll(camp.Id, FixedSession, FakeImage(), default);

        Assert.NotNull(checkedKey);
        Assert.NotEqual(oldKey, checkedKey);
        Assert.Equal(oldKey, refAtCheck);
        Assert.DoesNotContain(oldKey, uploadedAtCheck!);
        Assert.DoesNotContain(oldKey, files.Uploaded);
        Assert.DoesNotContain(oldKey, files.Deleted);
    }

    // ── (6) BK25 giữ nguyên: dòng sổ của ảnh mới có TRƯỚC khi object lên S3 ───────────────────────────
    [Fact]
    public async Task Enroll_GhiSo_TruocKhi_Upload()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t);
        var files = new FakeFileService();
        bool? ledgerExistedAtUpload = null;
        files.OnUpload = async key =>
            ledgerExistedAtUpload = await t.NewContext().FaceImages.AnyAsync(x => x.StorageKey == key);

        await NewController(t.NewContext(), cand, files, AiDetecting(1).Object)
            .Enroll(camp.Id, FixedSession, FakeImage(), default);

        Assert.True(ledgerExistedAtUpload);
    }

    // ── (7) ảnh bị loại: dọn theo thứ tự S3 TRƯỚC, sổ SAU; S3 lỗi → GIỮ dòng sổ (purger nhặt) ────────
    [Fact]
    public async Task Enroll_BiLoai_XoaS3TruocSoSau_S3Loi_GiuDongSo_VanTra400()
    {
        using var t = new CampaignTestDb();
        var (camp, cand) = Seed(t);
        var files = new FakeFileService();
        bool? ledgerExistedAtDelete = null;
        files.OnDelete = async key =>
            ledgerExistedAtDelete = await t.NewContext().FaceImages.AnyAsync(x => x.StorageKey == key);
        files.OnUpload = key => { files.FailDeleteOnKey = key; return Task.CompletedTask; };

        var result = await NewController(t.NewContext(), cand, files, AiDetecting(3).Object)
            .Enroll(camp.Id, FixedSession, FakeImage(), default);

        Assert.Equal("REFERENCE_MULTIPLE_FACES", Body(result).GetProperty("code").GetString());
        Assert.True(ledgerExistedAtDelete);                            // sổ còn lúc xoá S3 ⇒ S3 trước
        Assert.Equal(files.Uploaded, LedgerKeys(t));                   // S3 hỏng ⇒ dấu vết object còn lại
        Assert.Null(ReferenceKeyInDb(t));
    }

    // ── hợp đồng dây với AIService /face-detect ─────────────────────────────────────────────────────
    private sealed class DetectHandler : HttpMessageHandler
    {
        public string? Path;
        public string? Body;
        public string? Token;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Response = "{\"faceCount\":2,\"signals\":[\"multiple_faces\"]}";
        public TimeSpan Delay = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Token = request.Headers.TryGetValues("X-Internal-Token", out var v) ? v.Single() : null;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Response, Encoding.UTF8, "application/json")
            };
        }
    }

    private static AiServiceFaceVerifyClient Client(DetectHandler h, string? detectTimeoutSeconds = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Internal:Token"] = "tok-ac2",
            ["AiService:FaceDetectTimeoutSeconds"] = detectTimeoutSeconds
        }).Build();
        return new AiServiceFaceVerifyClient(
            new HttpClient(h) { BaseAddress = new Uri("http://ai.local") }, config,
            NullLogger<AiServiceFaceVerifyClient>.Instance);
    }

    // Tên khoá JSON bị khoá TỪNG KÝ TỰ: lệch tên ⇒ FastAPI 422 ⇒ controller mở cửa ⇒ cửa kiểm chết câm.
    [Fact]
    public async Task DetectAsync_HopDong_ImageKey_Token_VaDocFaceCount()
    {
        var h = new DetectHandler();
        var res = await Client(h).DetectAsync("campaigns/c/candidates/d/face-reference-ab.jpg");

        Assert.Equal("/api/v1/face-detect", h.Path);
        Assert.Equal("tok-ac2", h.Token);
        using var doc = JsonDocument.Parse(h.Body!);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "imageKey" }, props);
        Assert.Equal("campaigns/c/candidates/d/face-reference-ab.jpg", doc.RootElement.GetProperty("imageKey").GetString());
        Assert.Equal(2, res.FaceCount);
        Assert.Equal(new[] { "multiple_faces" }, res.Signals);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task DetectAsync_Non2xx_NemDownstream(HttpStatusCode status)
    {
        var h = new DetectHandler { Status = status, Response = "{\"detail\":\"x\"}" };
        await Assert.ThrowsAsync<DownstreamServiceException>(() => Client(h).DetectAsync("k"));
    }

    // Hết giờ là LỖI HẠ TẦNG (controller mở cửa), không phải người gọi huỷ.
    [Fact]
    public async Task DetectAsync_HetGio_NemDownstream()
    {
        var h = new DetectHandler { Delay = TimeSpan.FromSeconds(10) };
        await Assert.ThrowsAsync<DownstreamServiceException>(() => Client(h, "1").DetectAsync("k"));
    }

    // Người gọi tự huỷ ⇒ OperationCanceledException đi thẳng lên, KHÔNG bị đổi thành "lỗi hạ tầng".
    [Fact]
    public async Task DetectAsync_NguoiGoiHuy_KhongDoiThanhDownstream()
    {
        var h = new DetectHandler { Delay = TimeSpan.FromSeconds(10) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var ex = await Record.ExceptionAsync(() => Client(h, "30").DetectAsync("k", cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }
}
